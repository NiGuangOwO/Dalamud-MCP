using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Generic ImGui input injection. Nothing in this file knows about any other plugin: it works
/// purely in the coordinates, ids and key names that Dear ImGui itself exposes, so it applies to
/// every ImGui window in the process - Dalamud's own, this plugin's, and any third-party
/// plugin's - without referencing their assemblies, ids or commands.
///
/// <para>The mechanism is Dear ImGui's supported input queue: events are pushed with
/// <c>AddMousePosEvent</c> / <c>AddMouseButtonEvent</c> / <c>AddKeyEvent</c> /
/// <c>AddInputCharacter</c> and applied by ImGui on the next <c>NewFrame</c>, exactly as a real
/// backend would. No vtable is patched, no plugin code is called and no Win32 message is
/// synthesised into another process - the events enter the same pipeline the real mouse and
/// keyboard use.</para>
///
/// <para>Frames and timing. The hook runs from <c>UiBuilder.Draw</c>, which is inside the frame,
/// after <c>ImGui.NewFrame</c>. An event queued there is therefore consumed by the next frame's
/// <c>NewFrame</c>. Every request is expanded into a list of single-frame steps and exactly one
/// step runs per frame; that is what makes a click a genuine press-then-release with a frame in
/// between rather than a collapsed no-op, and what lets ImGui's trickle queue deliver a new mouse
/// position before anything depends on it.</para>
///
/// <para>Mouse position needs one extra care. Dalamud's Win32 input backend re-enqueues the real
/// OS cursor position every frame while <c>io.WantSetMousePos</c> is false, which would overwrite
/// an injected position. Each job therefore raises <c>io.WantSetMousePos</c> for its duration
/// (the same flag a real backend uses both to suppress that re-enqueue and to move the OS cursor
/// to the injected point) and restores the previous value, along with the previous cursor
/// position and the <c>AppAcceptingEvents</c> flag, when the last step has been consumed.</para>
/// </summary>
internal static class ImGuiInputTools
{
    private static readonly object Sync = new();
    private static readonly Queue<InjectionJob> Pending = new();

    private static GameServices? services;
    private static bool hooked;
    private static long jobCounter;

    /// <summary>
    /// Completed by the first <c>OnDraw</c> ever, which is the proof that the client really is
    /// running an ImGui frame loop. Injectors wait on it briefly before committing to a job, so a
    /// call made when no frame will ever come (out of game, or before the UI is up) is answered
    /// with a phrased error instead of hanging until the job timeout.
    /// </summary>
    private static readonly TaskCompletionSource<bool> FirstFrame =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// How long an injector waits for the frame loop to prove itself. In game the very next frame
    /// completes this, so the wait costs nothing; out of game it is the whole cost of the call.
    /// </summary>
    private const int FrameGraceMs = 2000;

    /// <summary>Largest grid a single hover sweep may cover, so one call cannot stall the client.</summary>
    private const int MaxGridCells = 4096;

    /// <summary>Characters pushed per frame by imgui_text.</summary>
    private const int TextCharsPerFrame = 16;

    // ------------------------------------------------------------------
    // Registration
    // ------------------------------------------------------------------

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        services = svc;
        Hook(svc);

        registry.Add(
            "imgui_state",
            "Read Dear ImGui's input and focus state",
            "Reports what Dear ImGui currently believes about the pointer and focus: frame count, " +
            "display size, mouse position, per-button down state, the WantCaptureMouse / " +
            "WantCaptureKeyboard / WantTextInput flags, and the hovered / active / nav window and " +
            "id. This is the ground truth for the imgui_* injection tools: read it before and " +
            "after a click to see exactly what changed. Read-only, and independent of which " +
            "plugin owns the window.",
            Json.Schema(),
            _ => ImGuiState());

        registry.Add(
            "imgui_windows",
            "List Dear ImGui windows",
            "Enumerates every Dear ImGui window in the process - Dalamud's, this plugin's, and " +
            "every other installed plugin's - with its name, id, position, size, and its " +
            "active / hidden / collapsed / focused flags. Coordinates are in the same space " +
            "imgui_mouse_click accepts. Filter with 'contains' to match a name fragment. " +
            "Read-only: it lists windows, it does not need to know who created them.",
            Json.Schema(
                ("contains", "string", "Only report windows whose name contains this text (case-insensitive)", false),
                ("onlyActive", "boolean", "Only report the active window and its focus order (default false)", false),
                ("onlyVisible", "boolean", "Skip windows that are hidden or have SkipItems set (default false)", false),
                ("max", "integer", "Maximum windows to report (default 200)", false)),
            args => ImGuiWindows(args));

        RegisterInjectors(registry);
    }

    public static void Unregister()
    {
        Unhook();
        lock (Sync)
        {
            foreach (var job in Pending)
                job.Done.TrySetResult(new JObject { ["cancelled"] = true });
            Pending.Clear();
        }
        services = null;
    }

    private static void Hook(GameServices svc)
    {
        var iface = svc.PluginInterface;
        if (iface is null || hooked) return;
        iface.UiBuilder.Draw += OnDraw;
        hooked = true;
    }

    private static void Unhook()
    {
        var iface = services?.PluginInterface;
        if (iface is null || !hooked) return;
        iface.UiBuilder.Draw -= OnDraw;
        hooked = false;
    }

    // ------------------------------------------------------------------
    // Tool surface
    // ------------------------------------------------------------------

    private static void RegisterInjectors(ToolRegistry registry)
    {
        registry.AddAsync(
            "imgui_mouse_move",
            "Move the ImGui pointer",
            "Injects a pointer position into Dear ImGui and lets it settle for the requested " +
            "number of frames, then reports what is hovered. Use this to aim before clicking, or " +
            "with imgui_state to probe what sits at a coordinate. The position is in ImGui " +
            "coordinates, which are the game window's client pixels (read displaySize from " +
            "imgui_state to sanity-check). Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "Target X in ImGui coordinates", true),
                ("y", "number", "Target Y in ImGui coordinates", true),
                ("settleFrames", "integer", "Frames to hold the position before sampling (default 2)", false),
                ("moveCursor", "boolean", "Also move the real OS cursor to the target (default true)", false),
                ("restoreCursor", "boolean", "Restore the real cursor when the job ends (default true)", false)),
            args => RunAsync(JobMouseMove(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_mouse_click",
            "Click with the ImGui pointer",
            "Injects a real press-and-release into Dear ImGui at a coordinate - optionally moving " +
            "there first. The down and up are delivered on separate frames, so the click is not " +
            "collapsed by ImGui's trickle queue. Reports the hovered window and id captured just " +
            "before the press, and a full state snapshot afterwards, so the caller can see what " +
            "was hit and what changed. Works on any ImGui window regardless of which plugin owns " +
            "it. Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "X in ImGui coordinates (omit to click where the pointer already is)", false),
                ("y", "number", "Y in ImGui coordinates (omit to click where the pointer already is)", false),
                ("button", "integer", "Mouse button: 0 left (default), 1 right, 2 middle", false),
                ("double", "boolean", "Also send a second press/release pair (default false)", false),
                ("settleFrames", "integer", "Frames to hold the position before pressing (default 3)", false),
                ("moveCursor", "boolean", "Also move the real OS cursor to the target (default true)", false),
                ("restoreCursor", "boolean", "Restore the real cursor when the job ends (default true)", false)),
            args => RunAsync(JobMouseClick(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_mouse_scroll",
            "Scroll with the ImGui pointer",
            "Injects a mouse wheel event into Dear ImGui, optionally moving the pointer to the " +
            "widget to scroll first. Deltas are in ImGui wheel units (one notch is 1.0, the same " +
            "value a real wheel reports). Mutating: it synthesises input.",
            Json.Schema(
                ("x", "number", "X in ImGui coordinates to scroll at (omit to scroll in place)", false),
                ("y", "number", "Y in ImGui coordinates to scroll at (omit to scroll in place)", false),
                ("deltaY", "number", "Vertical wheel delta in notches (default -3)", false),
                ("deltaX", "number", "Horizontal wheel delta in notches (default 0)", false),
                ("settleFrames", "integer", "Frames to hold the position before scrolling (default 2)", false)),
            args => RunAsync(JobMouseScroll(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_key_press",
            "Press a key in Dear ImGui",
            "Injects a complete key press into Dear ImGui, with optional Ctrl / Shift / Alt " +
            "modifiers held across the same frames a real chord would span. The key is named with " +
            "Dear ImGui's own key names (A, Enter, Escape, Tab, Delete, F1, LeftArrow, ...). Any " +
            "ImGui window in the process sees this exactly as it would see the physical key. " +
            "Mutating: it synthesises input.",
            Json.Schema(
                ("key", "string", "Dear ImGui key name, e.g. Enter, Escape, A, F1, LeftArrow", true),
                ("ctrl", "boolean", "Hold Ctrl for the press (default false)", false),
                ("shift", "boolean", "Hold Shift for the press (default false)", false),
                ("alt", "boolean", "Hold Alt for the press (default false)", false)),
            args => RunAsync(JobKeyPress(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_key",
            "Set a raw ImGui key state",
            "Injects a bare key down or key up into Dear ImGui with no pairing - useful for " +
            "holding a modifier or a movement key across several other calls. The key is named " +
            "with Dear ImGui's own key names. Remember to send the matching up. Mutating: it " +
            "synthesises input.",
            Json.Schema(
                ("key", "string", "Dear ImGui key name, e.g. LeftCtrl, A, Enter", true),
                ("down", "boolean", "True for key down, false for key up", true)),
            args => RunAsync(JobKey(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_text",
            "Type text into Dear ImGui",
            "Injects a string as character input into Dear ImGui, the same path the OS keyboard " +
            "uses for text entry, so IME-free text - including CJK - reaches whatever ImGui input " +
            "field has keyboard focus. Send it after click_focus / a click on the field. " +
            "Mutating: it synthesises input.",
            Json.Schema(
                ("text", "string", "Text to type", true),
                ("charsPerFrame", "integer", $"Characters per frame (1-{TextCharsPerFrame}, default {TextCharsPerFrame})", false)),
            args => RunAsync(JobText(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_hover_grid",
            "Sweep the ImGui pointer over a grid",
            "The generic way to discover what is inside an ImGui window without knowing which " +
            "plugin drew it. Moves the pointer to every cell of a cols x rows grid covering the " +
            "given rectangle, holding the position one frame and sampling the next, and returns " +
            "for each cell whether it is inside a window and which id is hovered. A single call " +
            "reconstructs the hit layout of any window - find the window rectangle with " +
            "imgui_windows, sweep it, and the cells where the hovered window or id changes are " +
            "the controls. rows=1 gives a horizontal scan line. Mutating: it synthesises input.",
            Json.Schema(
                ("x0", "number", "Left edge of the rectangle, ImGui coordinates", true),
                ("y0", "number", "Top edge of the rectangle, ImGui coordinates", true),
                ("x1", "number", "Right edge of the rectangle, ImGui coordinates", true),
                ("y1", "number", "Bottom edge of the rectangle, ImGui coordinates", true),
                ("cols", "integer", "Number of sample columns (default 8)", false),
                ("rows", "integer", "Number of sample rows (default 8)", false),
                ("settleFrames", "integer", "Frames to hold each position before sampling (default 1)", false)),
            args => RunAsync(JobHoverGrid(args)),
            mutating: true);

        registry.AddAsync(
            "imgui_wait",
            "Wait a number of ImGui frames",
            "Idles for a number of frames, letting the client redraw and any animation, popup or " +
            "layout change settle before the next observation. Read-only.",
            Json.Schema(
                ("frames", "integer", "Frames to wait (1-600, default 30)", false)),
            args => RunAsync(JobWait(args)));
    }

    // ------------------------------------------------------------------
    // Read-only snapshots
    // ------------------------------------------------------------------

    private static unsafe string? WindowName(ImGuiWindowPtr window) =>
        window.IsNull || window.Name == null ? null : Marshal.PtrToStringUTF8((nint)window.Name);

    /// <summary>Everything Dear ImGui knows about pointer, focus and capture right now.</summary>
    private static JObject Snapshot()
    {
        var ctx = ImGui.GetCurrentContext();
        var state = new JObject
        {
            ["frameCount"] = ctx.IsNull ? 0 : ctx.FrameCount,
            ["withinFrameScope"] = !ctx.IsNull && ctx.WithinFrameScope,
        };

        var io = ImGui.GetIO();
        state["displaySize"] = new JArray(io.DisplaySize.X, io.DisplaySize.Y);
        state["mousePos"] = new JArray(io.MousePos.X, io.MousePos.Y);
        state["mouseDelta"] = new JArray(io.MouseDelta.X, io.MouseDelta.Y);
        state["mouseDown"] = new JArray(io.MouseDown.ToArray().Select(d => (object)d).ToArray());
        state["mouseWheel"] = new JArray(io.MouseWheel, io.MouseWheelH);
        state["wantCaptureMouse"] = io.WantCaptureMouse;
        state["wantCaptureKeyboard"] = io.WantCaptureKeyboard;
        state["wantTextInput"] = io.WantTextInput;
        state["wantSetMousePos"] = io.WantSetMousePos;
        state["appAcceptingEvents"] = io.AppAcceptingEvents;

        if (!ctx.IsNull)
        {
            state["hoveredId"] = ctx.HoveredId;
            state["hoveredIdPreviousFrame"] = ctx.HoveredIdPreviousFrame;
            state["activeId"] = ctx.ActiveId;
            state["hoveredWindow"] = WindowName(ctx.HoveredWindow);
            state["hoveredWindowUnderMovingWindow"] = WindowName(ctx.HoveredWindowUnderMovingWindow);
            state["activeIdWindow"] = WindowName(ctx.ActiveIdWindow);
            state["navWindow"] = WindowName(ctx.NavWindow);
            state["mouseCursor"] = ctx.MouseCursor.ToString();
        }

        return state;
    }

    private static object ImGuiState()
    {
        if (ImGui.GetCurrentContext().IsNull)
            return Json.ToolError("Dear ImGui has no current context; the client UI is not up yet.");

        return Snapshot();
    }

    private static unsafe object ImGuiWindows(JObject args)
    {
        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull)
            return Json.ToolError("Dear ImGui has no current context; the client UI is not up yet.");

        var contains = args["contains"]?.Value<string>();
        var onlyActive = args["onlyActive"]?.Value<bool>() ?? false;
        var onlyVisible = args["onlyVisible"]?.Value<bool>() ?? false;
        var max = Math.Clamp(args["max"]?.Value<int>() ?? 200, 1, 2000);

        var rows = new JArray();
        var total = 0;

        foreach (var window in ctx.Windows)
        {
            if (window.IsNull) continue;
            total++;

            var name = WindowName(window) ?? string.Empty;
            if (!string.IsNullOrEmpty(contains) &&
                name.IndexOf(contains, StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (onlyVisible && (window.Hidden || window.SkipItems)) continue;
            if (onlyActive && !window.Active) continue;

            rows.Add(new JObject
            {
                ["name"] = name,
                ["id"] = window.ID,
                ["pos"] = new JArray(window.Pos.X, window.Pos.Y),
                ["size"] = new JArray(window.Size.X, window.Size.Y),
                ["sizeFull"] = new JArray(window.SizeFull.X, window.SizeFull.Y),
                ["contentSize"] = new JArray(window.ContentSize.X, window.ContentSize.Y),
                ["active"] = window.Active,
                ["wasActive"] = window.WasActive,
                ["hidden"] = window.Hidden,
                ["skipItems"] = window.SkipItems,
                ["collapsed"] = window.Collapsed,
                ["hasCloseButton"] = window.HasCloseButton,
                ["viewportOwned"] = window.ViewportOwned,
                ["lastFrameActive"] = window.LastFrameActive,
                ["focusOrder"] = window.FocusOrder,
                ["beginCount"] = window.BeginCount,
            });

            if (rows.Count >= max) break;
        }

        return new JObject
        {
            ["total"] = total,
            ["count"] = rows.Count,
            ["windows"] = rows,
        };
    }

    // ------------------------------------------------------------------
    // Frame hook
    // ------------------------------------------------------------------

    /// <summary>
    /// Runs inside the ImGui frame, after NewFrame. Exactly one step of the head job is executed
    /// per frame, which is what separates a press from its release and what lets a position
    /// settle before anything is read from it.
    /// </summary>
    private static void OnDraw()
    {
        // Reaching here at all is the proof that the client is pumping ImGui frames, which is what
        // lets an injector distinguish "the UI is up" from "no frame will ever come". Signalled
        // before the queue is inspected so it is recorded even on frames with no job pending.
        FirstFrame.TrySetResult(true);

        InjectionJob? job;
        lock (Sync) { job = Pending.Count > 0 ? Pending.Peek() : null; }
        if (job is null) return;

        var ctx = ImGui.GetCurrentContext();
        if (ctx.IsNull) return;

        if (job.TimedOut)
        {
            RestoreState(job);
            lock (Sync) { Pending.Dequeue(); }
            job.Done.TrySetResult(job.Result);
            return;
        }

        var io = ImGui.GetIO();

        if (!job.Began)
        {
            job.SavedWantSetMousePos = io.WantSetMousePos;
            job.SavedAppAccepting = io.AppAcceptingEvents;
            if (job.MoveCursor && GetCursorPos(out var p))
            {
                job.SavedCursor = p;
                job.HaveSavedCursor = true;
            }
            job.Began = true;
            job.Trace.Add("begin");
        }

        // Raised for the whole job: it both suppresses the backend's per-frame re-enqueue of the
        // real cursor position and makes the backend drive the OS cursor to the injected point,
        // so the two sources of truth converge instead of fighting.
        if (job.HoldWantSetMousePos) io.WantSetMousePos = true;
        io.SetAppAcceptingEvents(true);

        if (job.Step < job.Steps.Count)
        {
            var step = job.Steps[job.Step];
            job.Step++;
            try
            {
                step(job);
            }
            catch (Exception ex)
            {
                job.Result["error"] = $"{ex.GetType().Name}: {ex.Message}";
                RestoreState(job);
                lock (Sync) { Pending.Dequeue(); }
                job.Done.TrySetResult(job.Result);
            }
            return;
        }

        job.Result["endState"] = Snapshot();
        job.Result["injectedEvents"] = job.InjectedEvents;
        job.Result["trace"] = job.Trace;
        RestoreState(job);
        lock (Sync) { Pending.Dequeue(); }
        job.Done.TrySetResult(job.Result);
    }

    private static void RestoreState(InjectionJob job)
    {
        if (!job.Began) return;
        try
        {
            var ctx = ImGui.GetCurrentContext();
            if (!ctx.IsNull)
            {
                var io = ImGui.GetIO();
                io.WantSetMousePos = job.SavedWantSetMousePos;
                io.SetAppAcceptingEvents(job.SavedAppAccepting);
            }
            if (job.RestoreCursor && job.HaveSavedCursor)
                SetCursorPos(job.SavedCursor.X, job.SavedCursor.Y);
        }
        catch (Exception ex)
        {
            if (job.Result["error"] is null) job.Result["restoreError"] = ex.Message;
        }
        job.Trace.Add("end");
    }

    // ------------------------------------------------------------------
    // Jobs
    // ------------------------------------------------------------------

    private sealed class InjectionJob
    {
        public long Id;
        public string Kind = string.Empty;
        public bool MoveCursor = true;
        public bool RestoreCursor = true;
        public bool HoldWantSetMousePos = true;
        public int TimeoutMs = 15000;

        public readonly List<Action<InjectionJob>> Steps = new();
        public readonly JArray Trace = new();
        public readonly JObject Result = new();
        public readonly TaskCompletionSource<JObject> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Step;
        public bool Began;
        public bool TimedOut;
        public bool SavedWantSetMousePos;
        public bool SavedAppAccepting;
        public bool HaveSavedCursor;
        public POINT SavedCursor;
        public int InjectedEvents;
    }

    private static InjectionJob NewJob(string kind, int steps, int timeoutMs)
    {
        var job = new InjectionJob
        {
            Id = System.Threading.Interlocked.Increment(ref jobCounter),
            Kind = kind,
        };
        job.Result["kind"] = kind;
        job.Result["jobId"] = job.Id;
        // One step per frame, plus a frame to consume the last event and a frame of slack.
        job.TimeoutMs = Math.Max(timeoutMs, (steps + 8) * 40);
        return job;
    }

    private static async Task<object?> RunAsync(InjectionJob job)
    {
        if (services is null) return Json.ToolError("ImGui input tools are not initialised.");
        if (!hooked) return Json.ToolError("The ImGui frame hook is not installed.");

        // Prove the frame loop is live before committing to a job. Without this an injector called
        // out of game enqueues itself, is never picked up by OnDraw, and only answers when the job
        // timeout expires - a whole idle timeout for what is really an immediate "not available".
        if (!FirstFrame.Task.IsCompleted)
        {
            var frame = await Task.WhenAny(FirstFrame.Task, Task.Delay(FrameGraceMs)).ConfigureAwait(false);
            if (frame != FirstFrame.Task)
            {
                return Json.ToolError(
                    $"imgui_{job.Kind} is not available: Dear ImGui did not render a frame within " +
                    $"{FrameGraceMs} ms, so there is nothing to inject into. The client may be " +
                    "loading, zoned, or the UI may be down.");
            }
        }

        lock (Sync) { Pending.Enqueue(job); }

        var finished = await Task.WhenAny(job.Done.Task, Task.Delay(job.TimeoutMs))
            .ConfigureAwait(false);

        if (finished != job.Done.Task)
        {
            job.TimedOut = true;
            return Json.ToolError(
                $"imgui_{job.Kind} timed out after {job.TimeoutMs} ms waiting for ImGui frames. " +
                "The client may be loading, zoned, or the UI may be down.");
        }

        return job.Done.Task.Result;
    }

    // -- step builders -------------------------------------------------

    private static Action<InjectionJob> StepMove(float x, float y) => job =>
    {
        ImGui.GetIO().AddMousePosEvent(x, y);
        job.InjectedEvents++;
        job.Trace.Add($"mousePos {x:0.##},{y:0.##}");
    };

    private static Action<InjectionJob> StepButton(int button, bool down) => job =>
    {
        ImGui.GetIO().AddMouseButtonEvent(button, down);
        job.InjectedEvents++;
        job.Trace.Add($"mouse{button} {(down ? "down" : "up")}");
    };

    private static Action<InjectionJob> StepWheel(float dx, float dy) => job =>
    {
        ImGui.GetIO().AddMouseWheelEvent(dx, dy);
        job.InjectedEvents++;
        job.Trace.Add($"wheel {dx:0.##},{dy:0.##}");
    };

    private static Action<InjectionJob> StepKey(ImGuiKey key, bool down) => job =>
    {
        ImGui.GetIO().AddKeyEvent(key, down);
        job.InjectedEvents++;
        job.Trace.Add($"key {key} {(down ? "down" : "up")}");
    };

    private static Action<InjectionJob> StepCapture(string label) => job =>
    {
        job.Result[label] = Snapshot();
        job.Trace.Add($"snapshot {label}");
    };

    private static void AddSettle(InjectionJob job, float x, float y, int frames)
    {
        for (var i = 0; i < frames; i++) job.Steps.Add(StepMove(x, y));
    }

    private static int ReadSettle(JObject args, int fallback, int min, int max) =>
        Math.Clamp(args["settleFrames"]?.Value<int>() ?? fallback, min, max);

    private static float RequireFloat(JObject args, string name)
    {
        var token = args[name];
        if (token is null || token.Type == JTokenType.Null)
            throw new ToolException($"missing required parameter: {name}");
        return token.Value<float>();
    }

    // -- concrete jobs -------------------------------------------------

    private static InjectionJob JobMouseMove(JObject args)
    {
        var x = RequireFloat(args, "x");
        var y = RequireFloat(args, "y");
        var settle = ReadSettle(args, 2, 1, 240);

        var job = NewJob("mouse_move", settle + 2, 20000);
        job.MoveCursor = args["moveCursor"]?.Value<bool>() ?? true;
        job.RestoreCursor = args["restoreCursor"]?.Value<bool>() ?? true;
        job.Result["target"] = new JArray(x, y);

        AddSettle(job, x, y, settle);
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobMouseClick(JObject args)
    {
        var hasPos = args["x"] is not null && args["x"]!.Type != JTokenType.Null
                  && args["y"] is not null && args["y"]!.Type != JTokenType.Null;
        var x = hasPos ? RequireFloat(args, "x") : 0f;
        var y = hasPos ? RequireFloat(args, "y") : 0f;
        var button = Math.Clamp(args["button"]?.Value<int>() ?? 0, 0, 4);
        var dbl = args["double"]?.Value<bool>() ?? false;
        var settle = ReadSettle(args, 3, 1, 240);

        var steps = (hasPos ? settle : 0) + (dbl ? 4 : 2) + 2;
        var job = NewJob("mouse_click", steps, 20000);
        job.MoveCursor = args["moveCursor"]?.Value<bool>() ?? true;
        job.RestoreCursor = args["restoreCursor"]?.Value<bool>() ?? true;
        job.Result["button"] = button;
        job.Result["double"] = dbl;
        if (hasPos) job.Result["target"] = new JArray(x, y);

        if (hasPos) AddSettle(job, x, y, settle);

        // Sample immediately before the press: this is the window/id the press will land in.
        job.Steps.Add(StepCapture("beforePress"));
        job.Steps.Add(StepButton(button, true));
        job.Steps.Add(StepButton(button, false));
        if (dbl)
        {
            job.Steps.Add(StepButton(button, true));
            job.Steps.Add(StepButton(button, false));
        }
        // One more frame so the final release has been applied before the end state is read.
        job.Steps.Add(StepCapture("afterRelease"));
        return job;
    }

    private static InjectionJob JobMouseScroll(JObject args)
    {
        var hasPos = args["x"] is not null && args["x"]!.Type != JTokenType.Null
                  && args["y"] is not null && args["y"]!.Type != JTokenType.Null;
        var x = hasPos ? RequireFloat(args, "x") : 0f;
        var y = hasPos ? RequireFloat(args, "y") : 0f;
        var dx = args["deltaX"]?.Value<float>() ?? 0f;
        var dy = args["deltaY"]?.Value<float>() ?? -3f;
        var settle = ReadSettle(args, 2, 1, 240);

        var job = NewJob("mouse_scroll", (hasPos ? settle : 0) + 3, 20000);
        job.MoveCursor = args["moveCursor"]?.Value<bool>() ?? true;
        job.RestoreCursor = args["restoreCursor"]?.Value<bool>() ?? true;
        job.Result["delta"] = new JArray(dx, dy);

        if (hasPos) AddSettle(job, x, y, settle);
        job.Steps.Add(StepCapture("beforeWheel"));
        job.Steps.Add(StepWheel(dx, dy));
        job.Steps.Add(StepCapture("afterWheel"));
        return job;
    }

    private static ImGuiKey ParseKey(JObject args)
    {
        var name = args["key"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(name))
            throw new ToolException("missing required parameter: key");
        if (!Enum.TryParse<ImGuiKey>(name, ignoreCase: true, out var key) || key == ImGuiKey.None)
            throw new ToolException(
                $"unknown ImGui key '{name}'. Use Dear ImGui key names such as A, Enter, Escape, " +
                "Tab, Space, Delete, LeftArrow, F1, LeftCtrl.");
        return key;
    }

    private static InjectionJob JobKeyPress(JObject args)
    {
        var key = ParseKey(args);
        var ctrl = args["ctrl"]?.Value<bool>() ?? false;
        var shift = args["shift"]?.Value<bool>() ?? false;
        var alt = args["alt"]?.Value<bool>() ?? false;

        var mods = new List<ImGuiKey>();
        if (ctrl) mods.Add(ImGuiKey.LeftCtrl);
        if (shift) mods.Add(ImGuiKey.LeftShift);
        if (alt) mods.Add(ImGuiKey.LeftAlt);

        var job = NewJob("key_press", mods.Count * 2 + 3, 20000);
        job.Result["key"] = key.ToString();
        job.Result["modifiers"] = new JArray(mods.Select(m => (object)m.ToString()).ToArray());

        foreach (var mod in mods) job.Steps.Add(StepKey(mod, true));
        job.Steps.Add(StepKey(key, true));
        job.Steps.Add(StepKey(key, false));
        foreach (var mod in mods) job.Steps.Add(StepKey(mod, false));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobKey(JObject args)
    {
        var key = ParseKey(args);
        var down = args["down"]?.Value<bool>()
            ?? throw new ToolException("missing required parameter: down");

        var job = NewJob("key", 3, 20000);
        job.Result["key"] = key.ToString();
        job.Result["down"] = down;
        job.Steps.Add(StepKey(key, down));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    private static InjectionJob JobText(JObject args)
    {
        var text = args["text"]?.Value<string>();
        if (string.IsNullOrEmpty(text))
            throw new ToolException("missing required parameter: text");

        var perFrame = Math.Clamp(
            args["charsPerFrame"]?.Value<int>() ?? TextCharsPerFrame, 1, TextCharsPerFrame);

        var chunks = new List<List<Rune>>();
        var current = new List<Rune>();
        foreach (var rune in text.EnumerateRunes())
        {
            current.Add(rune);
            if (current.Count < perFrame) continue;
            chunks.Add(current);
            current = new List<Rune>();
        }
        if (current.Count > 0) chunks.Add(current);

        var job = NewJob("text", chunks.Count + 2, 30000);
        job.Result["text"] = text;
        job.Result["runeCount"] = text.EnumerateRunes().Count();
        job.Result["frames"] = chunks.Count;

        job.Steps.Add(StepCapture("beforeText"));
        foreach (var chunk in chunks)
        {
            var local = chunk;
            job.Steps.Add(j =>
            {
                var io = ImGui.GetIO();
                foreach (var rune in local)
                {
                    io.AddInputCharacter(rune);
                    j.InjectedEvents++;
                }
                j.Trace.Add($"chars {local.Count}");
            });
        }
        job.Steps.Add(StepCapture("afterText"));
        return job;
    }

    private static InjectionJob JobHoverGrid(JObject args)
    {
        var x0 = RequireFloat(args, "x0");
        var y0 = RequireFloat(args, "y0");
        var x1 = RequireFloat(args, "x1");
        var y1 = RequireFloat(args, "y1");
        var cols = Math.Clamp(args["cols"]?.Value<int>() ?? 8, 1, 256);
        var rows = Math.Clamp(args["rows"]?.Value<int>() ?? 8, 1, 256);
        var settle = ReadSettle(args, 1, 1, 10);

        if (cols * rows > MaxGridCells)
            throw new ToolException(
                $"grid too large: {cols}x{rows} exceeds the {MaxGridCells}-cell limit per call.");

        var job = NewJob("hover_grid", (settle + 1) * cols * rows + 2, 60000);
        job.Result["rect"] = new JArray(x0, y0, x1, y1);
        job.Result["cols"] = cols;
        job.Result["rows"] = rows;

        var cells = new JArray();
        job.Result["cells"] = cells;

        for (var r = 0; r < rows; r++)
        {
            var cy = rows == 1 ? y0 : y0 + (y1 - y0) * r / (rows - 1);
            for (var c = 0; c < cols; c++)
            {
                var cx = cols == 1 ? x0 : x0 + (x1 - x0) * c / (cols - 1);
                var px = cx;
                var py = cy;

                for (var s = 0; s < settle; s++) job.Steps.Add(StepMove(px, py));

                job.Steps.Add(j =>
                {
                    var snap = Snapshot();
                    cells.Add(new JObject
                    {
                        ["x"] = px,
                        ["y"] = py,
                        ["hoveredWindow"] = snap["hoveredWindow"],
                        ["hoveredId"] = snap["hoveredId"],
                        ["hoveredIdPreviousFrame"] = snap["hoveredIdPreviousFrame"],
                        ["activeId"] = snap["activeId"],
                        ["wantCaptureMouse"] = snap["wantCaptureMouse"],
                    });
                });
            }
        }

        job.Steps.Add(StepCapture("endState"));
        return job;
    }

    private static InjectionJob JobWait(JObject args)
    {
        var frames = Math.Clamp(args["frames"]?.Value<int>() ?? 30, 1, 600);
        var job = NewJob("wait", frames + 1, 30000);
        job.HoldWantSetMousePos = false;
        job.MoveCursor = false;
        job.RestoreCursor = false;
        job.Result["frames"] = frames;

        for (var i = 0; i < frames; i++) job.Steps.Add(j => j.Trace.Add("tick"));
        job.Steps.Add(StepCapture("state"));
        return job;
    }

    // ------------------------------------------------------------------
    // Win32 cursor
    // ------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetCursorPos(int x, int y);
}
