using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.System.Input;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Sends a bounded tap through FFXIV's own keyboard device instead of Dear ImGui.
/// KeyboardDevice.SendKey constructs and submits a key-down/key-up SendInput pair in one
/// native call, so every successfully dispatched tap is self-closing.
/// </summary>
internal static class GameKeyInputTools
{
    private const int MaxRepeats = 8;
    private const int MaxIntervalFrames = 30;
    private const int ForegroundTimeoutMs = 1000;
    private static readonly Regex KeyNamePattern = new(
        @"\A[A-Za-z][A-Za-z0-9_]*\z",
        RegexOptions.CultureInvariant);
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.AddAsync(
            "press_game_key",
            "Press a native game key",
            "Sends a bounded keyboard tap through FFXIV's native KeyboardDevice, not Dear ImGui. " +
            "Each repeat is one complete key-down/key-up pair; no key is left held. The game window must be foreground " +
            "unless activateWindow is requested. Refuses while logged out/loading, in the wrong expected territory, " +
            "when the native keyboard is unavailable, or when the key was already down. Supports exact SeVirtualKey names " +
            "such as UP, DOWN, LEFT, RIGHT, RETURN, SPACE, ESCAPE, W, A, S, D, KEY_1, NUMPAD1 and F1. " +
            "Verify the resulting game/UI state before repeating; dispatch is not proof of a business outcome.",
            Json.Schema(
                ("key", "string", "Exact FFXIV SeVirtualKey enum name, case-insensitive; numeric names are refused", true),
                ("repeat", "integer", $"Number of complete taps (1-{MaxRepeats}, default 1)", false),
                ("intervalFrames", "integer", $"Framework ticks between taps (1-{MaxIntervalFrames}, default 2)", false),
                ("expectedTerritoryId", "integer", "Optional guard: refuse unless the current territory has this id", false),
                ("activateWindow", "boolean", "Bring the verified game window to the foreground before sending (default false)", false),
                ("dryRun", "boolean", "Validate the client, window and native keyboard and read the current key state without sending (default false)", false)),
            args => PressAsync(svc, args),
            mutating: true);

        registry.AddAsync(
            "hold_game_key",
            "Hold a native game key",
            "Sends a bounded Win32 key-down, keeps it held for 50-2000ms, then always sends key-up in a finally path. " +
            "Use this for continuous movement or held UI controls that a one-shot KeyState tap cannot exercise. " +
            "The verified game window must be foreground unless activateWindow is requested. Refuses logged-out/loading clients, " +
            "wrong expected territory, unavailable native keyboard state, and keys already down. Verify position/UI state afterwards.",
            Json.Schema(
                ("key", "string", "Exact FFXIV SeVirtualKey enum name, case-insensitive", true),
                ("durationMs", "integer", "Hold duration in milliseconds (50-2000, default 250)", false),
                ("expectedTerritoryId", "integer", "Optional guard: refuse unless the current territory has this id", false),
                ("activateWindow", "boolean", "Bring the verified game window to the foreground before holding (default false)", false),
                ("dryRun", "boolean", "Validate the client, window and native keyboard and read the current key state without sending (default false)", false)),
            args => HoldAsync(svc, args),
            mutating: true);
    }

    private static async Task<object?> PressAsync(GameServices svc, JObject args)
    {
        var key = ParseKey(args);
        var repeats = OptionalInt(args, "repeat", 1, 1, MaxRepeats);
        var intervalFrames = OptionalInt(args, "intervalFrames", 2, 1, MaxIntervalFrames);
        var expectedTerritoryId = OptionalNullableInt(args, "expectedTerritoryId", 1, ushort.MaxValue);
        var activateWindow = OptionalBool(args, "activateWindow", false);
        var dryRun = OptionalBool(args, "dryRun", false);

        var hwnd = GameWindowTools.WindowHandle(svc);
        GameWindowTools.Validate(hwnd);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        var tapsSent = 0;
        try
        {
            var probe = await svc.Framework.Run(
                () => Probe(svc, key, expectedTerritoryId),
                timeout.Token).ConfigureAwait(false);

            var foregroundPid = ForegroundProcessId();
            var gameInForeground = foregroundPid == Environment.ProcessId;
            if (dryRun)
                return Result(key, probe.RawValue, probe, dryRun: true, activated: false, gameInForeground,
                    repeats, tapsSent, intervalFrames, 0);

            if (probe.RawValue != 0)
                throw new ToolException($"key {key} is already down (raw state {probe.RawValue}); refusing to synthesize over physical input");

            var started = Stopwatch.StartNew();
            var activated = false;
            for (var index = 0; index < repeats; index++)
            {
                var foreground = await EnsureForegroundAsync(svc, activateWindow).ConfigureAwait(false);
                activated |= foreground.Changed;

                await svc.Framework.Run(() => SendOnce(key), timeout.Token).ConfigureAwait(false);
                tapsSent++;

                // SendKey emits both transitions in one native SendInput call. One tick gives the
                // game time to consume them, after which the raw key state should be clear again.
                await svc.Framework.DelayTicks(1, timeout.Token).ConfigureAwait(false);
                var after = await svc.Framework.Run(() => ReadKeyState(svc, key, expectedTerritoryId), timeout.Token)
                    .ConfigureAwait(false);
                if (after.RawValue != 0)
                {
                    await svc.Framework.Run(() => ClearKeyState(key), timeout.Token).ConfigureAwait(false);
                    await svc.Framework.DelayTicks(1, timeout.Token).ConfigureAwait(false);
                    var cleared = await svc.Framework.Run(
                        () => ReadKeyState(svc, key, expectedTerritoryId),
                        timeout.Token).ConfigureAwait(false);
                    if (cleared.RawValue != 0)
                        throw new ToolException($"native key state for {key} did not clear after a one-shot tap (raw state {cleared.RawValue})");
                }

                if (index + 1 < repeats)
                    await svc.Framework.DelayTicks(intervalFrames, timeout.Token).ConfigureAwait(false);
            }

            var final = await svc.Framework.Run(
                () => ReadKeyState(svc, key, expectedTerritoryId),
                timeout.Token).ConfigureAwait(false);
            if (final.RawValue != 0)
                throw new ToolException($"key {key} remained down after the final tap (raw state {final.RawValue})");

            return Result(key, probe.RawValue, final, dryRun: false, activated, gameInForeground: true,
                repeats, tapsSent, intervalFrames, started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw new ToolException(
                "native key input timed out waiting for the game framework; each dispatched tap is a complete key-down/key-up pair");
        }
        catch (ToolException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ToolException($"native key input failed after {tapsSent} tap(s): {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<object?> HoldAsync(GameServices svc, JObject args)
    {
        var key = ParseKey(args);
        var durationMs = OptionalInt(args, "durationMs", 250, 50, 2000);
        var expectedTerritoryId = OptionalNullableInt(args, "expectedTerritoryId", 1, ushort.MaxValue);
        var activateWindow = OptionalBool(args, "activateWindow", false);
        var dryRun = OptionalBool(args, "dryRun", false);

        var hwnd = GameWindowTools.WindowHandle(svc);
        GameWindowTools.Validate(hwnd);

        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(durationMs + 8000));
        await Gate.WaitAsync(timeout.Token).ConfigureAwait(false);
        var downSent = false;
        var upSent = false;
        try
        {
            var probe = await svc.Framework.Run(
                () => Probe(svc, key, expectedTerritoryId),
                timeout.Token).ConfigureAwait(false);
            var gameInForeground = BelongsToGame(GetForegroundWindow());
            if (dryRun)
                return HoldResult(key, durationMs, probe.RawValue, probe.RawValue, dryRun: true, activated: false,
                    gameInForeground, keyDown: false, keyUp: false, foregroundAtRelease: gameInForeground, elapsedMs: 0);
            if (probe.RawValue != 0)
                throw new ToolException($"key {key} is already down (raw state {probe.RawValue}); refusing to synthesize over physical input");

            var focused = await EnsureForegroundAsync(svc, activateWindow).ConfigureAwait(false);
            var started = Stopwatch.StartNew();
            SendKeyboardInput(key, keyUp: false);
            downSent = true;
            await Task.Delay(durationMs, timeout.Token).ConfigureAwait(false);
            var foregroundAtRelease = BelongsToGame(GetForegroundWindow());
            SendKeyboardInput(key, keyUp: true);
            upSent = true;

            await svc.Framework.DelayTicks(2, timeout.Token).ConfigureAwait(false);
            var after = await svc.Framework.Run(
                () => ReadKeyState(svc, key, expectedTerritoryId),
                timeout.Token).ConfigureAwait(false);
            if (after.RawValue != 0)
            {
                await svc.Framework.Run(() => ClearKeyState(key), timeout.Token).ConfigureAwait(false);
                await svc.Framework.DelayTicks(1, timeout.Token).ConfigureAwait(false);
                var cleared = await svc.Framework.Run(
                    () => ReadKeyState(svc, key, expectedTerritoryId),
                    timeout.Token).ConfigureAwait(false);
                if (cleared.RawValue != 0)
                    throw new ToolException($"native key state for {key} did not clear after hold (raw state {cleared.RawValue})");
                after = cleared;
            }

            return HoldResult(key, durationMs, probe.RawValue, after.RawValue, dryRun: false, focused.Changed,
                gameInForeground: true, keyDown: true, keyUp: true, foregroundAtRelease,
                started.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw new ToolException("held key input timed out; the key-up path was attempted in cleanup");
        }
        finally
        {
            if (downSent && !upSent)
            {
                try { SendKeyboardInput(key, keyUp: true); }
                catch { /* Preserve the primary failure; a process/window failure may prevent cleanup. */ }
            }
            Gate.Release();
        }
    }

    private static SeVirtualKey ParseKey(JObject args)
    {
        if (args["key"]?.Type != JTokenType.String)
            throw new ToolException("key must be an exact SeVirtualKey name such as UP, DOWN, LEFT, RIGHT or RETURN");

        var text = args.Value<string>("key")?.Trim() ?? string.Empty;
        if (!KeyNamePattern.IsMatch(text) || !Enum.TryParse<SeVirtualKey>(text, ignoreCase: true, out var key) ||
            !Enum.IsDefined(typeof(SeVirtualKey), key))
        {
            throw new ToolException($"unknown native key '{text}'; use an exact SeVirtualKey name such as UP, DOWN, LEFT, RIGHT or RETURN");
        }

        if (key == SeVirtualKey.NO_KEY || key is SeVirtualKey.LBUTTON or SeVirtualKey.RBUTTON or SeVirtualKey.MBUTTON or
            SeVirtualKey.XBUTTON1 or SeVirtualKey.XBUTTON2 || key.ToString().StartsWith("PAD_", StringComparison.Ordinal))
        {
            throw new ToolException("press_game_key accepts keyboard keys only; mouse and gamepad SeVirtualKey values are refused");
        }

        return key;
    }

    private static void SendKeyboardInput(SeVirtualKey key, bool keyUp)
    {
        const uint inputKeyboard = 1;
        const uint keyEventExtendedKey = 0x0001;
        const uint keyEventKeyUp = 0x0002;

        var input = new NativeInput { Type = inputKeyboard };
        input.Data.Keyboard.VirtualKey = (ushort)key;
        input.Data.Keyboard.Flags = (IsExtendedKey(key) ? keyEventExtendedKey : 0) | (keyUp ? keyEventKeyUp : 0);
        var sent = SendInput(1, new[] { input }, Marshal.SizeOf<NativeInput>());
        if (sent != 1)
            throw new ToolException($"Win32 SendInput accepted {sent} of 1 keyboard event (error {Marshal.GetLastWin32Error()})");
    }

    private static bool IsExtendedKey(SeVirtualKey key) => key is
        SeVirtualKey.PRIOR or SeVirtualKey.NEXT or SeVirtualKey.END or SeVirtualKey.HOME or
        SeVirtualKey.LEFT or SeVirtualKey.UP or SeVirtualKey.RIGHT or SeVirtualKey.DOWN or
        SeVirtualKey.INSERT or SeVirtualKey.DELETE or SeVirtualKey.DIVIDE or SeVirtualKey.NUMLOCK or
        SeVirtualKey.LWIN or SeVirtualKey.RWIN or
        SeVirtualKey.APPS or SeVirtualKey.PRINT or SeVirtualKey.SNAPSHOT;

    private static int OptionalInt(JObject args, string name, int fallback, int min, int max)
    {
        var token = args[name];
        if (token is null) return fallback;
        if (token.Type != JTokenType.Integer)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        var value = token.Value<long>();
        if (value < min || value > max)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        return (int)value;
    }

    private static int? OptionalNullableInt(JObject args, string name, int min, int max)
    {
        var token = args[name];
        if (token is null) return null;
        if (token.Type != JTokenType.Integer)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        var value = token.Value<long>();
        if (value < min || value > max)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        return (int)value;
    }

    private static bool OptionalBool(JObject args, string name, bool fallback)
    {
        var token = args[name];
        if (token is null) return fallback;
        if (token.Type != JTokenType.Boolean)
            throw new ToolException($"{name} must be a boolean");
        return token.Value<bool>();
    }

    private static unsafe KeyProbe Probe(GameServices svc, SeVirtualKey key, int? expectedTerritoryId)
    {
        EnsureGameContext(svc, expectedTerritoryId);
        var keyboard = ResolveKeyboard(key);
        return new KeyProbe(keyboard.Connected, keyboard.RawValue, keyboard.KeyStateLength);
    }

    private static unsafe KeyProbe ReadKeyState(GameServices svc, SeVirtualKey key, int? expectedTerritoryId)
    {
        EnsureGameContext(svc, expectedTerritoryId);
        var keyboard = ResolveKeyboard(key);
        return new KeyProbe(keyboard.Connected, keyboard.RawValue, keyboard.KeyStateLength);
    }

    private static void EnsureGameContext(GameServices svc, int? expectedTerritoryId)
    {
        if (svc.Framework.IsFrameworkUnloading)
            throw new ToolException("the game framework is unloading");
        if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer is null)
            throw new ToolException("not logged in or the local player is not loaded");
        if (expectedTerritoryId is { } expected && svc.ClientState.TerritoryType != expected)
            throw new ToolException(
                $"territory guard failed: expected {expected}, current {svc.ClientState.TerritoryType}");
    }

    private static unsafe void SendOnce(SeVirtualKey key)
    {
        var keyboard = ResolveKeyboard(key);
        if (keyboard.RawValue != 0)
            throw new ToolException($"key {key} is already down (raw state {keyboard.RawValue}); refusing to send");
        keyboard.Device->SendKey(key);
    }

    private static unsafe void ClearKeyState(SeVirtualKey key)
    {
        var keyboard = ResolveKeyboard(key);
        keyboard.Data->KeyState[keyboard.KeyIndex] = KeyStateFlags.None;
    }

    private static unsafe NativeKeyboard ResolveKeyboard(SeVirtualKey key)
    {
        var manager = InputDeviceManager.Instance();
        if (manager == null)
            throw new ToolException("FFXIV InputDeviceManager is unavailable");
        var device = manager->KeyboardDevice;
        if (device == null)
            throw new ToolException("FFXIV keyboard device is unavailable");
        if (!device->GetIsKeyboardConnected())
            throw new ToolException("FFXIV reports that no keyboard device is connected");
        var data = device->GetData();
        if (data == null)
            throw new ToolException("FFXIV keyboard state is unavailable");

        var keyIndex = Convert.ToInt32(key, CultureInfo.InvariantCulture);
        var state = data->KeyState;
        if ((uint)keyIndex >= (uint)state.Length)
            throw new ToolException(
                $"native key {key} maps outside the bounded keyboard state ({state.Length} entries)");
        return new NativeKeyboard(device, data, keyIndex, (int)state[keyIndex], state.Length);
    }

    private static async Task<(bool Changed, bool Foreground)> EnsureForegroundAsync(GameServices svc, bool activate)
    {
        var hwnd = GameWindowTools.WindowHandle(svc);
        GameWindowTools.Validate(hwnd);
        if (IsIconic(hwnd))
        {
            if (!activate)
                throw new ToolException("the game window is minimized; restore it first or pass activateWindow=true");
            ShowWindowAsync(hwnd, SwRestore);
        }

        if (BelongsToGame(GetForegroundWindow())) return (false, true);
        if (!activate)
            throw new ToolException("the game window is not foreground; pass activateWindow=true for an authorized game-input call");

        ForceForeground(hwnd);
        var deadline = Environment.TickCount64 + ForegroundTimeoutMs;
        while (Environment.TickCount64 <= deadline)
        {
            await Task.Delay(50).ConfigureAwait(false);
            if (BelongsToGame(GetForegroundWindow())) return (true, true);
        }

        throw new ToolException("the game window could not be brought to the foreground; no key input was sent");
    }

    private static bool BelongsToGame(IntPtr hwnd) =>
        hwnd != IntPtr.Zero &&
        GetWindowThreadProcessId(hwnd, out var pid) != 0 &&
        pid == (uint)Environment.ProcessId;

    private static void ForceForeground(IntPtr hwnd)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == IntPtr.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, attach: true);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, attach: false);
        }
    }

    private static int ForegroundProcessId()
    {
        var hwnd = GetForegroundWindow();
        return hwnd != IntPtr.Zero && GetWindowThreadProcessId(hwnd, out var pid) != 0 ? (int)pid : 0;
    }

    private static JObject Result(
        SeVirtualKey key,
        int rawBefore,
        KeyProbe probe,
        bool dryRun,
        bool activated,
        bool gameInForeground,
        int repeats,
        int tapsSent,
        int intervalFrames,
        long elapsedMs) => new()
        {
            ["key"] = key.ToString(),
            ["keyCode"] = Convert.ToInt32(key, CultureInfo.InvariantCulture),
            ["keyboardConnected"] = probe.KeyboardConnected,
            ["keyStateLength"] = probe.KeyStateLength,
            ["rawBefore"] = rawBefore,
            ["rawAfter"] = probe.RawValue,
            ["dryRun"] = dryRun,
            ["gameInForeground"] = gameInForeground,
            ["activatedWindow"] = activated,
            ["repeatsRequested"] = repeats,
            ["tapsSent"] = tapsSent,
            ["intervalFrames"] = intervalFrames,
            ["elapsedMs"] = elapsedMs,
            ["inputPath"] = "FFXIVClientStructs.InputDeviceManager.KeyboardDevice.SendKey",
            ["delivery"] = "one complete SendInput key-down/key-up pair per tap",
        };

    private static JObject HoldResult(
        SeVirtualKey key,
        int durationMs,
        int rawBefore,
        int rawAfter,
        bool dryRun,
        bool activated,
        bool gameInForeground,
        bool keyDown,
        bool keyUp,
        bool foregroundAtRelease,
        long elapsedMs) => new()
        {
            ["key"] = key.ToString(),
            ["keyCode"] = Convert.ToInt32(key, CultureInfo.InvariantCulture),
            ["durationMs"] = durationMs,
            ["rawBefore"] = rawBefore,
            ["rawAfter"] = rawAfter,
            ["dryRun"] = dryRun,
            ["gameInForeground"] = gameInForeground,
            ["activatedWindow"] = activated,
            ["keyDownDispatched"] = keyDown,
            ["keyUpDispatched"] = keyUp,
            ["foregroundAtRelease"] = foregroundAtRelease,
            ["elapsedMs"] = elapsedMs,
            ["inputPath"] = "Win32.SendInput key-down/hold/key-up",
        };

    private readonly record struct KeyProbe(bool KeyboardConnected, int RawValue, int KeyStateLength);

    private readonly unsafe struct NativeKeyboard
    {
        public NativeKeyboard(KeyboardDevice* device, KeyboardInputData* data, int keyIndex, int rawValue, int keyStateLength)
        {
            Device = device;
            Data = data;
            KeyIndex = keyIndex;
            RawValue = rawValue;
            KeyStateLength = keyStateLength;
        }

        public KeyboardDevice* Device { get; }
        public KeyboardInputData* Data { get; }
        public int KeyIndex { get; }
        public int RawValue { get; }
        public int KeyStateLength { get; }
        public bool Connected => Device->GetIsKeyboardConnected();
    }

    private const int SwRestore = 9;

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hwnd);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, NativeInput[] inputs, int inputSize);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeInput
    {
        public uint Type;
        public NativeInputUnion Data;
        public NativeKeyboardInput Keyboard => Data.Keyboard;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct NativeInputUnion
    {
        [FieldOffset(0)] public NativeKeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeKeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }
}
