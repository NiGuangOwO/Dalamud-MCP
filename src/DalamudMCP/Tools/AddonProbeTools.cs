using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Frame-aligned, read-only observations; pointers are resolved anew each frame.</summary>
internal sealed class AddonProbeTools : IDisposable
{
    private readonly GameServices svc;
    private readonly CancellationTokenSource lifetime = new();
    private int sampling;

    public AddonProbeTools(ToolRegistry registry, GameServices svc)
    {
        this.svc = svc;
        AddonDispatchTools.Register(registry);
        var schema = Json.Schema(
            ("addon", "string", "Exact internal name of a loaded addon, e.g. Hummer.", true),
            ("componentId", "integer", "Optional top-level component node id; scopes nodeIds to that component's UldManager.", false),
            ("componentPath", "array of integer", "Exact component path from list_addon_elements, outermost first (0-40 ids). Empty means top-level. Do not combine with componentId.", false),
            ("nodeIds", "array of integer", "Top-level UldManager node ids (1-32 unique ids). Component children are deliberately excluded to avoid ambiguous ids.", true));
        registry.Add("get_addon_node_snapshot", "Read precise addon node state",
            "Reads local and screen geometry, native transform matrices, origins, parent ids, visibility, node addresses, animation timelines and the addon's scalar values together on the framework thread. No input or memory writes.",
            schema, Read);
        var traceSchema = (JObject)schema.DeepClone();
        ((JObject)traceSchema["properties"]!).Add("frames", new JObject { ["type"] = "integer", ["description"] = "Number of frames to observe, 1-600 (default 180)." });
        ((JObject)traceSchema["properties"]!).Add("intervalFrames", new JObject { ["type"] = "integer", ["description"] = "Sample every N frames, 1-60 (default 1)." });
        registry.AddAsync("sample_addon_nodes", "Sample addon nodes over consecutive frames",
            "Collects frame-aligned snapshots without blocking the game. One sampler at a time, 600 frames maximum, 20 second timeout. Stops if the addon closes or changes identity; unload cancels and removes the listener.",
            traceSchema, args => Sample(args));
        registry.AddAsync("click_addon_when_node_matches", "Click once when a node reaches a width range",
            "Observes each framework frame and dispatches one UI event on a width range, transformed node alignment, or local rotation range. Default right-edge/center matching preserves gauge behavior; matchAxis x/y compares configurable node anchors for moving controls. Position matching rejects rotated/reflected geometry; rotation matching uses explicit local radians without wraparound. The clicked control must be visible and enabled. Integer addon-value guards are checked in the same frame. Bounded to 600 frames / 20 seconds; replacement and disabling mutating tools cancel the request. This tool does not press-and-hold: callers starting movement must handle timeout/stop themselves.",
            Json.Schema(
                ("addon", "string", "Exact internal addon name.", true),
                ("observeNodeId", "integer", "Top-level node whose width is observed.", true),
                ("clickNodeId", "integer", "Top-level visible and enabled node to click once.", true),
                ("componentPath", "array of integer", "Optional exact component path from list_addon_elements (0-40 ids); applies to every observed/clicked node.", false),
                ("minWidth", "integer", "Inclusive minimum width, 0-65535; required without targetNodeId.", false),
                ("maxWidth", "integer", "Inclusive maximum width, 0-65535; required without targetNodeId.", false),
                ("targetNodeId", "integer", "Optional top-level target marker. Match the observed right edge against its transformed horizontal center.", false),
                ("targetFraction", "number", "Central fraction of the marker width to accept, greater than 0 and at most 1 (default 0.5). Scales with UI zoom.", false),
                ("matchAxis", "string", "Match rightEdge (default), x, y, or rotation. x/y requires targetNodeId; rotation uses local radians and minRotation/maxRotation instead of width/target matching.", false),
                ("minRotation", "number", "For rotation: inclusive minimum local angle in radians, -pi to pi. No wraparound.", false),
                ("maxRotation", "number", "For rotation: inclusive maximum local angle in radians, -pi to pi. No wraparound.", false),
                ("observeAnchor", "number", "For x/y: fraction along observed node, 0-1 (default 0).", false),
                ("targetAnchor", "number", "For x/y: fraction along target node, 0-1 (default 0).", false),
                ("guardValueIndex", "integer", "Optional integer AtkValue index, 0-31; requires guardValue or guardValues.", false),
                ("guardValue", "integer", "Expected integer AtkValue at guardValueIndex; input is sent only while it matches.", false),
                ("guardValues", "array of integer", "Alternative to guardValue: 1-16 accepted integer values at guardValueIndex.", false),
                ("frames", "integer", "Maximum frames to wait, 1-600 (default 180).", false),
                ("event", "string", "UI event name accepted by click_addon_element (default buttonPress).", false),
                ("param", "integer", "Explicit event parameter, if needed.", false)),
            ClickWhen, mutating: true);
    }

    private static uint[] Ids(JObject args)
    {
        var ids = (args["nodeIds"] as JArray)?.Select(v => v.Value<uint>()).Distinct().ToArray();
        if (ids is null || ids.Length is < 1 or > 32)
            throw new ToolException("nodeIds must contain 1-32 unique unsigned node ids");
        return ids;
    }

    private static unsafe AtkUnitBase* Resolve(string name)
    {
        var unit = UiTools.ResolveAddon(name, null);
        return unit != null && string.Equals(unit->NameString, name, StringComparison.Ordinal) && unit->IsReady ? unit : null;
    }

    internal static uint[]? ReadComponentPath(JObject args)
    {
        if (args["componentPath"] == null) return null;
        if (args["componentId"] != null) throw new ToolException("use componentPath or componentId, not both");
        if (args["componentPath"] is not JArray path || path.Count > 40)
            throw new ToolException("componentPath must be an array of 0-40 unsigned node ids");
        var result = new uint[path.Count];
        for (var i = 0; i < path.Count; i++)
        {
            if (path[i].Type != JTokenType.Integer || !uint.TryParse(path[i].ToString(), out result[i]))
                throw new ToolException("componentPath must contain only unsigned integer node ids");
        }
        return result;
    }

    internal static unsafe AtkResNode* FindNode(AtkUnitBase* unit, uint id, uint? componentId = null, uint[]? componentPath = null)
    {
        var owner = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!owner.HasValue) return null;
        var uld = owner.Value.UldManager;
        var path = componentPath ?? (componentId.HasValue ? new[] { componentId.Value } : Array.Empty<uint>());
        var visited = new HashSet<nuint>();
        foreach (var parentId in path)
        {
            var parent = CheckboxTools.Find(uld, parentId);
            if (parent == null || !visited.Add((nuint)parent)) return null;
            var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)parent, out _);
            if (!node.HasValue || (int)node.Value.Type < 1000) return null;
            var component = MemoryProbe.TryReadStruct<AtkComponentBase>((ulong)node.Value.Component, out _);
            if (!component.HasValue || (nint)component.Value.OwnerNode != (nint)parent) return null;
            uld = component.Value.UldManager;
        }
        return CheckboxTools.Find(uld, id);
    }

    private static unsafe JObject Snapshot(string name, uint[] ids, uint? componentId = null, uint[]? componentPath = null)
    {
        var unit = Resolve(name);
        if (unit == null) return new JObject { ["available"] = false, ["addon"] = name };
        var nodes = new JArray();
        foreach (var id in ids)
        {
            var node = FindNode(unit, id, componentId, componentPath);
            if (node == null) { nodes.Add(new JObject { ["nodeId"] = id, ["available"] = false }); continue; }
            var timeline = node->Timeline;
            var events = new JArray();
            for (var e = node->AtkEventManager.Event; e != null && events.Count < 32; e = e->NextEvent)
                events.Add(new JObject { ["type"] = e->State.EventType.ToString(), ["param"] = e->Param, ["listener"] = $"0x{(nuint)e->Listener:X}" });
            nodes.Add(new JObject
            {
                ["nodeId"] = id, ["available"] = true, ["address"] = $"0x{(nuint)node:X}",
                ["type"] = node->Type.ToString(), ["x"] = node->X, ["y"] = node->Y,
                ["screenX"] = node->ScreenX, ["screenY"] = node->ScreenY,
                ["originX"] = node->OriginX, ["originY"] = node->OriginY,
                ["rotation"] = node->Rotation,
                ["parentNodeId"] = node->ParentNode == null ? null : (long?)node->ParentNode->NodeId,
                ["transform"] = new JObject
                {
                    ["m11"] = node->Transform.M11, ["m12"] = node->Transform.M12,
                    ["m21"] = node->Transform.M21, ["m22"] = node->Transform.M22,
                },
                ["width"] = node->Width, ["height"] = node->Height,
                ["scaleX"] = node->ScaleX, ["scaleY"] = node->ScaleY,
                ["visible"] = (node->NodeFlags & NodeFlags.Visible) != 0,
                ["enabled"] = (node->NodeFlags & NodeFlags.Enabled) != 0,
                ["events"] = events,
                ["timeline"] = timeline == null ? null : new JObject
                {
                    ["frameTime"] = timeline->FrameTime, ["parentFrameTime"] = timeline->ParentFrameTime,
                    ["activeLabelId"] = timeline->ActiveLabelId, ["labelEndFrame"] = timeline->LabelEndFrameIdx,
                    ["flags"] = (byte)timeline->Flags,
                },
            });
        }
        var values = new JArray();
        for (var i = 0; i < Math.Min((int)unit->AtkValuesCount, 32); i++)
        {
            var value = &unit->AtkValues[i];
            JToken? scalar = value->Type switch
            {
                AtkValueType.Int => new JValue(value->Int),
                AtkValueType.UInt => new JValue(value->UInt),
                AtkValueType.Float => new JValue(value->Float),
                AtkValueType.Bool => new JValue(value->Byte != 0),
                _ => null,
            };
            if (scalar != null) values.Add(new JObject { ["index"] = i, ["type"] = value->Type.ToString(), ["value"] = scalar });
        }
        return new JObject
        {
            ["available"] = true, ["addon"] = name, ["addonId"] = unit->Id,
            ["address"] = $"0x{(nuint)unit:X}", ["visible"] = unit->IsVisible, ["nodes"] = nodes,
            ["values"] = values,
            ["valueCount"] = unit->AtkValuesCount,
        };
    }

    private static object Read(JObject args) => Snapshot(args.Value<string>("addon") ?? "", Ids(args), args.Value<uint?>("componentId"), ReadComponentPath(args));

    private Task<object?> ClickWhen(JObject args)
    {
        foreach (var key in new[] { "addon", "observeNodeId", "clickNodeId" })
            if (args[key] is null || args[key]!.Type == JTokenType.Null) throw new ToolException($"{key} is required");
        var marker = args.Value<uint?>("targetNodeId");
        var axis = args.Value<string>("matchAxis") ?? "rightEdge";
        if (axis is not ("rightEdge" or "x" or "y" or "rotation") || (axis is "x" or "y" && marker == null))
            throw new ToolException("matchAxis must be rightEdge, x, y or rotation; x/y requires targetNodeId");
        if (axis == "rotation")
        {
            if (marker != null || args["minWidth"] != null || args["maxWidth"] != null ||
                args["minRotation"]?.Type is not (JTokenType.Float or JTokenType.Integer) ||
                args["maxRotation"]?.Type is not (JTokenType.Float or JTokenType.Integer) ||
                !NodeScreenMatch.RotationMatches(args.Value<double>("minRotation"), args.Value<double>("minRotation"), args.Value<double>("maxRotation")))
                throw new ToolException("rotation requires only minRotation/maxRotation in [-pi, pi], minimum <= maximum; targetNodeId and widths are incompatible");
        }
        else if (args["minRotation"] != null || args["maxRotation"] != null)
            throw new ToolException("minRotation/maxRotation requires matchAxis=rotation");
        foreach (var key in new[] { "observeAnchor", "targetAnchor" })
        {
            var anchor = args.Value<double?>(key) ?? 0;
            if (!double.IsFinite(anchor) || anchor is < 0 or > 1) throw new ToolException($"{key} must be 0-1");
        }
        if (axis != "rotation" && marker == null)
        {
            if (args["minWidth"] == null || args["maxWidth"] == null) throw new ToolException("minWidth/maxWidth or targetNodeId is required");
            var min = args.Value<int>("minWidth");
            var max = args.Value<int>("maxWidth");
            if (min < 0 || max > ushort.MaxValue || min > max) throw new ToolException("invalid width range");
        }
        else if (marker != null)
        {
            var fraction = args.Value<double?>("targetFraction") ?? 0.5;
            if (!double.IsFinite(fraction) || fraction <= 0 || fraction > 1) throw new ToolException("targetFraction must be greater than 0 and at most 1");
        }
        var guardIndex = args.Value<int?>("guardValueIndex");
        var hasGuardValue = args["guardValue"] is { Type: not JTokenType.Null };
        var hasGuardValues = args["guardValues"] is { Type: not JTokenType.Null };
        if (guardIndex is < 0 or > 31 || (hasGuardValue && hasGuardValues) || guardIndex.HasValue != (hasGuardValue || hasGuardValues))
            throw new ToolException("guardValueIndex (0-31) requires exactly one of guardValue or guardValues");
        if (hasGuardValues && (args["guardValues"] is not JArray accepted || accepted.Count is < 1 or > 16 || accepted.Any(v => v.Type != JTokenType.Integer)))
            throw new ToolException("guardValues must contain 1-16 integers");
        var observe = args.Value<uint>("observeNodeId");
        var click = args.Value<uint>("clickNodeId");
        var sampleArgs = (JObject)args.DeepClone();
        if (hasGuardValue) sampleArgs["guardValues"] = new JArray(args.Value<long>("guardValue"));
        sampleArgs["nodeIds"] = new JArray(observe, click);
        if (marker.HasValue) ((JArray)sampleArgs["nodeIds"]!).Add(marker.Value);
        var clickArgs = new JObject
        {
            ["addon"] = args["addon"], ["nodeId"] = click,
            ["event"] = args.Value<string>("event") ?? "buttonPress",
        };
        if (args["param"] is { Type: not JTokenType.Null } param) clickArgs["param"] = param;
        if (args["componentPath"] != null) clickArgs["componentPath"] = args["componentPath"]!.DeepClone();
        return Sample(sampleArgs, clickArgs);
    }

    private async Task<object?> Sample(JObject args, JObject? clickArgs = null)
    {
        var ids = Ids(args);
        var name = args.Value<string>("addon") ?? "";
        var componentId = args.Value<uint?>("componentId");
        var componentPath = ReadComponentPath(args);
        var frames = args.Value<int?>("frames") ?? 180;
        var interval = args.Value<int?>("intervalFrames") ?? 1;
        if (frames is < 1 or > 600 || interval is < 1 or > 60)
            throw new ToolException("frames must be 1-600; intervalFrames must be 1-60");
        if (Interlocked.CompareExchange(ref sampling, 1, 0) != 0)
            throw new ToolException("another addon sampler is active");
        var done = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var samples = new JArray();
        var timer = Stopwatch.StartNew();
        var frame = 0;
        string? identity = null;
        var reason = "completed";
        object? clickResult = null;
        void Tick(IFramework _)
        {
            if (done.Task.IsCompleted) return;
            try
            {
                if (lifetime.IsCancellationRequested) { done.TrySetCanceled(); return; }
                var snapshot = Snapshot(name, ids, componentId, componentPath);
                var currentIdentity = $"{snapshot["address"]}:{snapshot["addonId"]}";
                if (snapshot.Value<bool>("available") != true || (identity != null && identity != currentIdentity))
                {
                    reason = "addon_unavailable_or_replaced";
                    done.TrySetResult(null);
                    return;
                }
                identity ??= currentIdentity;
                if (frame % interval == 0)
                    samples.Add(new JObject { ["frame"] = frame, ["elapsedMs"] = timer.Elapsed.TotalMilliseconds, ["state"] = snapshot });
                if (clickArgs != null)
                {
                    if (!svc.Config.AllowMutatingTools) { reason = "mutating_tools_disabled"; done.TrySetResult(null); return; }
                    var nodes = (JArray)snapshot["nodes"]!;
                    var observed = nodes.OfType<JObject>().First(n => n.Value<uint>("nodeId") == args.Value<uint>("observeNodeId"));
                    var target = nodes.OfType<JObject>().First(n => n.Value<uint>("nodeId") == args.Value<uint>("clickNodeId"));
                    var markerId = args.Value<uint?>("targetNodeId");
                    var axis = args.Value<string>("matchAxis") ?? "rightEdge";
                    var marker = markerId.HasValue ? nodes.OfType<JObject>().First(n => n.Value<uint>("nodeId") == markerId.Value) : null;
                    var fraction = args.Value<double?>("targetFraction") ?? 0.5;
                    var matches = axis == "rotation"
                        ? observed["rotation"] != null && NodeScreenMatch.RotationMatches(observed.Value<double>("rotation"), args.Value<double>("minRotation"), args.Value<double>("maxRotation"))
                        : marker != null
                        ? axis == "rightEdge" ? NodeScreenMatch.Matches(Screen(observed), Screen(marker), fraction)
                            : NodeScreenMatch.AnchorsMatch(Screen(observed, axis == "y"), Screen(marker, axis == "y"), fraction,
                                args.Value<double?>("observeAnchor") ?? 0, args.Value<double?>("targetAnchor") ?? 0)
                        : observed.Value<int>("width") >= args.Value<int>("minWidth") && observed.Value<int>("width") <= args.Value<int>("maxWidth");
                    var guardIndex = args.Value<int?>("guardValueIndex");
                    var guard = guardIndex.HasValue ? ((JArray)snapshot["values"]!).OfType<JObject>().FirstOrDefault(v => v.Value<int>("index") == guardIndex.Value) : null;
                    var guardMatches = !guardIndex.HasValue || (guard != null && guard.Value<string>("type") is "Int" or "UInt" && ((JArray)args["guardValues"]!).Any(v => v.Value<long>() == guard.Value<long>("value")));
                    if (snapshot.Value<bool>("visible") && observed.Value<bool>("available") && observed.Value<bool>("visible") &&
                        target.Value<bool>("available") && target.Value<bool>("visible") && target.Value<bool>("enabled") && matches && guardMatches)
                    {
                        clickResult = UiTools.ClickAddonElement(clickArgs);
                        reason = "triggered";
                        frame++;
                        done.TrySetResult(null);
                        return;
                    }
                    // Keep a small tail for diagnostics without a large automation response.
                    if (samples.Count > 16) samples.RemoveAt(0);
                }
                if (++frame >= frames) done.TrySetResult(null);
            }
            catch (Exception ex) { done.TrySetException(ex); }
        }
        try
        {
            await svc.Framework.RunOnFrameworkThread(() => svc.Framework.Update += Tick);
            await done.Task.WaitAsync(TimeSpan.FromSeconds(20), lifetime.Token);
            return new JObject { ["addon"] = name, ["reason"] = reason, ["framesObserved"] = frame, ["samples"] = samples, ["clickResult"] = clickResult == null ? null : JToken.FromObject(clickResult) };
        }
        finally
        {
            svc.Framework.Update -= Tick;
            Volatile.Write(ref sampling, 0);
        }
    }

    private static ScreenNode Screen(JObject node, bool vertical = false) => new(
        node.Value<float>(vertical ? "screenY" : "screenX"), node.Value<float>(vertical ? "height" : "width"), node["transform"]?.Value<float>(vertical ? "m22" : "m11") ?? 0,
        node["transform"]?.Value<float>("m12") ?? 0, node["transform"]?.Value<float>("m21") ?? 0,
        node.Value<bool>("available") && node.Value<bool>("visible"));

    public void Dispose() => lifetime.Cancel();
}
