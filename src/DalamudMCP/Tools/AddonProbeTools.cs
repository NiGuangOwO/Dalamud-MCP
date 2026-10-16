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
        var schema = Json.Schema(
            ("addon", "string", "Exact internal name of a loaded addon, e.g. Hummer.", true),
            ("componentId", "integer", "Optional top-level component node id; scopes nodeIds to that component's UldManager.", false),
            ("nodeIds", "array of integer", "Top-level UldManager node ids (1-32 unique ids). Component children are deliberately excluded to avoid ambiguous ids.", true));
        registry.Add("get_addon_node_snapshot", "Read precise addon node state",
            "Reads local geometry, scale, visibility, node addresses and animation timeline fields on the framework thread. No input or memory writes.",
            schema, Read);
        var traceSchema = (JObject)schema.DeepClone();
        ((JObject)traceSchema["properties"]!).Add("frames", new JObject { ["type"] = "integer", ["description"] = "Number of frames to observe, 1-600 (default 180)." });
        ((JObject)traceSchema["properties"]!).Add("intervalFrames", new JObject { ["type"] = "integer", ["description"] = "Sample every N frames, 1-60 (default 1)." });
        registry.AddAsync("sample_addon_nodes", "Sample addon nodes over consecutive frames",
            "Collects frame-aligned snapshots without blocking the game. One sampler at a time, 600 frames maximum, 20 second timeout. Stops if the addon closes or changes identity; unload cancels and removes the listener.",
            traceSchema, args => Sample(args));
        registry.AddAsync("click_addon_when_node_matches", "Click once when a node reaches a width range",
            "Observes an addon on each framework frame and dispatches one UI event when the observed node is visible and its width enters the requested inclusive range. The target must also be visible and enabled. Bounded to 600 frames / 20 seconds; addon replacement and disabling mutating tools cancel the request. Useful for latency-sensitive native UI controls.",
            Json.Schema(
                ("addon", "string", "Exact internal addon name.", true),
                ("observeNodeId", "integer", "Top-level node whose width is observed.", true),
                ("clickNodeId", "integer", "Top-level visible and enabled node to click once.", true),
                ("minWidth", "integer", "Inclusive minimum observed width, 0-65535.", true),
                ("maxWidth", "integer", "Inclusive maximum observed width, 0-65535.", true),
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

    internal static unsafe AtkResNode* FindNode(AtkUnitBase* unit, uint id, uint? componentId = null)
    {
        var uld = &unit->UldManager;
        if (componentId is { } parentId)
        {
            var parent = FindNode(unit, parentId);
            if (parent == null || (int)parent->Type < 1000) return null;
            var component = ((AtkComponentNode*)parent)->Component;
            if (component == null) return null;
            uld = &component->UldManager;
        }
        for (var i = 0; i < Math.Min((int)uld->NodeListCount, 4096); i++)
        {
            var node = uld->NodeList[i];
            if (node != null && node->NodeId == id) return node;
        }
        return null;
    }

    private static unsafe JObject Snapshot(string name, uint[] ids, uint? componentId = null)
    {
        var unit = Resolve(name);
        if (unit == null) return new JObject { ["available"] = false, ["addon"] = name };
        var nodes = new JArray();
        foreach (var id in ids)
        {
            var node = FindNode(unit, id, componentId);
            if (node == null) { nodes.Add(new JObject { ["nodeId"] = id, ["available"] = false }); continue; }
            var timeline = node->Timeline;
            var events = new JArray();
            for (var e = node->AtkEventManager.Event; e != null && events.Count < 32; e = e->NextEvent)
                events.Add(new JObject { ["type"] = e->State.EventType.ToString(), ["param"] = e->Param, ["listener"] = $"0x{(nuint)e->Listener:X}" });
            nodes.Add(new JObject
            {
                ["nodeId"] = id, ["available"] = true, ["address"] = $"0x{(nuint)node:X}",
                ["type"] = node->Type.ToString(), ["x"] = node->X, ["y"] = node->Y,
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
        return new JObject
        {
            ["available"] = true, ["addon"] = name, ["addonId"] = unit->Id,
            ["address"] = $"0x{(nuint)unit:X}", ["visible"] = unit->IsVisible, ["nodes"] = nodes,
        };
    }

    private static object Read(JObject args) => Snapshot(args.Value<string>("addon") ?? "", Ids(args), args.Value<uint?>("componentId"));

    private Task<object?> ClickWhen(JObject args)
    {
        foreach (var key in new[] { "addon", "observeNodeId", "clickNodeId", "minWidth", "maxWidth" })
            if (args[key] is null || args[key]!.Type == JTokenType.Null) throw new ToolException($"{key} is required");
        var min = args.Value<int>("minWidth");
        var max = args.Value<int>("maxWidth");
        if (min < 0 || max > ushort.MaxValue || min > max) throw new ToolException("invalid width range");
        var observe = args.Value<uint>("observeNodeId");
        var click = args.Value<uint>("clickNodeId");
        var sampleArgs = (JObject)args.DeepClone();
        sampleArgs["nodeIds"] = new JArray(observe, click);
        var clickArgs = new JObject
        {
            ["addon"] = args["addon"], ["nodeId"] = click,
            ["event"] = args.Value<string>("event") ?? "buttonPress",
        };
        if (args["param"] is { Type: not JTokenType.Null } param) clickArgs["param"] = param;
        return Sample(sampleArgs, clickArgs);
    }

    private async Task<object?> Sample(JObject args, JObject? clickArgs = null)
    {
        var ids = Ids(args);
        var name = args.Value<string>("addon") ?? "";
        var componentId = args.Value<uint?>("componentId");
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
                var snapshot = Snapshot(name, ids, componentId);
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
                    if (snapshot.Value<bool>("visible") && observed.Value<bool>("available") && observed.Value<bool>("visible") &&
                        target.Value<bool>("available") && target.Value<bool>("visible") && target.Value<bool>("enabled") &&
                        observed.Value<int>("width") >= args.Value<int>("minWidth") && observed.Value<int>("width") <= args.Value<int>("maxWidth"))
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

    public void Dispose() => lifetime.Cancel();
}
