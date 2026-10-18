using System;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class CheckboxTools
{
    public static void Register(ToolRegistry registry) => registry.Add(
        "set_addon_checkbox", "Set a native addon checkbox",
        "Reads a guarded native CheckBox and sets its checked state using the installed SDK, then sends its registered addon ButtonClick notification once. Idempotent: unchanged state sends no input. Only visible, enabled controls with matching ownership and an unambiguous addon listener are accepted. dryRun reports current state without input; expectedChecked optionally guards the observed prior state. Does not accept dialogs or press confirmation buttons.",
        Json.Schema(
            ("addon", "string", "Exact loaded addon name.", true),
            ("nodeId", "integer", "Top-level CheckBox node id from list_addon_elements.", true),
            ("checked", "boolean", "Desired native checked state; must be a JSON boolean.", true),
            ("componentId", "integer", "Optional top-level component; scope nodeId to its ULD array.", false),
            ("expectedChecked", "boolean", "Optional guard: refuse if the current state differs.", false),
            ("dryRun", "boolean", "Report the control and current state without changing it.", false)),
        Set, mutating: true);

    internal static bool TryReadCheckbox(nint address, out nint component, out bool isChecked)
    {
        component = 0; isChecked = false;
        var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)address, out _);
        if (!node.HasValue || (int)node.Value.Type < 1000 ||
            UiTools.ComponentKindOf((AtkComponentNode*)address) != ComponentType.CheckBox) return false;
        var control = MemoryProbe.TryReadStruct<AtkComponentCheckBox>((ulong)node.Value.Component, out _);
        if (!control.HasValue || (nint)control.Value.OwnerNode != address) return false;
        component = (nint)node.Value.Component;
        isChecked = control.Value.IsChecked;
        return true;
    }

    internal static bool StrictBoolean(JObject args, string key, bool? fallback = null)
    {
        if (args[key] == null && fallback.HasValue) return fallback.Value;
        if (args[key]?.Type != JTokenType.Boolean) throw new ToolException(key + " must be a JSON boolean");
        return args.Value<bool>(key);
    }

    internal static AtkResNode* Find(AtkUldManager uld, uint id)
    {
        if (uld.NodeList == null || uld.NodeListCount is 0 or > 4096) return null;
        AtkResNode* found = null;
        for (var i = 0; i < uld.NodeListCount; i++)
        {
            var address = MemoryProbe.TryReadStruct<nint>((ulong)uld.NodeList + (ulong)i * 8, out _);
            if (!address.HasValue) return null;
            if (address.Value == 0) continue;
            var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)address.Value, out _);
            if (!node.HasValue) return null;
            if (node.Value.NodeId != id) continue;
            if (found != null) return null;
            found = (AtkResNode*)address.Value;
        }
        return found;
    }

    private static object Set(JObject args)
    {
        var desired = StrictBoolean(args, "checked");
        var dryRun = StrictBoolean(args, "dryRun", false);
        var expected = args["expectedChecked"] != null ? StrictBoolean(args, "expectedChecked") : (bool?)null;
        var name = args.Value<string>("addon") ?? "";
        if (name.Length == 0 || args["nodeId"]?.Type != JTokenType.Integer)
            throw new ToolException("addon and integer nodeId are required");
        var unit = UiTools.ResolveAddon(name, null);
        if (unit == null || !unit->IsReady || !unit->IsVisible || unit->NameString != name)
            throw new ToolException("exact addon is unavailable, not ready or hidden");
        var unitCopy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!unitCopy.HasValue) throw new ToolException("unreadable addon");
        var uld = unitCopy.Value.UldManager;
        if (args.Value<uint?>("componentId") is { } scope)
        {
            var parent = Find(uld, scope);
            var parentCopy = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)parent, out _);
            if (!parentCopy.HasValue || (int)parentCopy.Value.Type < 1000)
                throw new ToolException("component scope is unavailable");
            var parentControl = MemoryProbe.TryReadStruct<AtkComponentBase>((ulong)parentCopy.Value.Component, out _);
            if (!parentControl.HasValue || parentControl.Value.OwnerNode != (AtkComponentNode*)parent)
                throw new ToolException("invalid component scope ownership");
            uld = parentControl.Value.UldManager;
        }
        var node = Find(uld, args.Value<uint>("nodeId"));
        if (!TryReadCheckbox((nint)node, out var component, out var before))
            throw new ToolException("node is not a readable CheckBox owned by this component node");
        var current = node;
        for (var depth = 0; current != null; depth++)
        {
            var copy = MemoryProbe.TryReadStruct<AtkResNode>((ulong)current, out _);
            if (depth >= 64 || !copy.HasValue || (copy.Value.NodeFlags & NodeFlags.Visible) == 0)
                throw new ToolException("checkbox has hidden, unreadable or cyclic ancestry");
            if (current == node && (copy.Value.NodeFlags & NodeFlags.Enabled) == 0)
                throw new ToolException("checkbox is disabled");
            current = copy.Value.ParentNode;
        }
        if (expected.HasValue && expected.Value != before) throw new ToolException("expectedChecked no longer matches");
        // Capture the registered notification before SetChecked can update UI state.
        var next = node->AtkEventManager.Event;
        uint? notification = null;
        for (var i = 0; next != null; i++)
        {
            var entry = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (i >= 64 || !entry.HasValue) throw new ToolException("invalid checkbox event chain");
            if (entry.Value.State.EventType == AtkEventType.ButtonClick && entry.Value.Listener == (AtkEventListener*)unit)
            {
                if (notification.HasValue) throw new ToolException("ambiguous checkbox addon notifications");
                notification = entry.Value.Param;
            }
            next = entry.Value.NextEvent;
        }
        if (!notification.HasValue) throw new ToolException("checkbox has no registered addon ButtonClick notification");
        var addonId = unit->Id; var nodeId = node->NodeId;
        var changed = !dryRun && before != desired;
        if (changed)
        {
            ((AtkComponentCheckBox*)component)->SetChecked(desired);
            if (!TryReadCheckbox((nint)node, out var afterComponent, out var after) || afterComponent != component || after != desired)
                throw new ToolException("native checkbox did not reach the requested state; notification was not sent");
            byte* evt = stackalloc byte[0x40]; byte* data = stackalloc byte[0x40];
            new Span<byte>(evt, 0x40).Clear(); new Span<byte>(data, 0x40).Clear();
            *(AtkResNode**)evt = node;
            *(AtkEventTarget**)(evt + 8) = (AtkEventTarget*)node;
            *(AtkEventListener**)(evt + 16) = (AtkEventListener*)unit;
            unit->ReceiveEvent(AtkEventType.ButtonClick, (int)notification.Value, (AtkEvent*)evt, (AtkEventData*)data);
        }
        return new JObject { ["addon"] = name, ["addonId"] = addonId, ["nodeId"] = nodeId,
            ["dryRun"] = dryRun, ["before"] = before, ["checked"] = changed ? desired : before,
            ["changed"] = changed, ["notificationDispatched"] = changed, ["eventParam"] = notification.Value };
    }
}
