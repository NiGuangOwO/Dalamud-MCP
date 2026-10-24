using System;
using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class DropdownTools
{
    public static void Register(ToolRegistry registry)
    {
        JObject Common() => Json.Schema(
            ("addon", "string", "Exact visible, ready native addon name.", true),
            ("nodeId", "integer", "DropDownList component node id.", true),
            ("componentPath", "array of integer", "Exact outer component scope, outermost first (0-40 ids).", false),
            ("expectedAddonId", "integer", "Optional guard on loaded addon identity.", false));
        var read = Common();
        read["properties"]!["offset"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 1024 };
        read["properties"]!["max"] = new JObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100 };
        registry.Add("get_addon_dropdown", "Read a native dropdown's options",
            "Reads an exactly scoped DropDownList's selected index, open/pending state and a bounded page of indexed labels. " +
            "Supports at most 1024 options, including closed lists. Unknown labels remain null and scanIncomplete=true. " +
            "No input or native function invocation. Recheck current options and identity before selecting.", read, args =>
            {
                var target = Resolve(args);
                var result = ReadAt(target.Node, MapTools.Integer(args, "offset", 0, 0, 1024), MapTools.Integer(args, "max", 40, 1, 100));
                result["addon"] = target.Name; result["addonId"] = target.Id; return result;
            });
        var openSchema = Common();
        openSchema["properties"]!["open"] = new JObject { ["type"] = "boolean" };
        openSchema["properties"]!["expectedOpen"] = new JObject { ["type"] = "boolean" };
        openSchema["properties"]!["dryRun"] = new JObject { ["type"] = "boolean" };
        ((JArray)openSchema["required"]!).Add("open");
        registry.Add("set_addon_dropdown_open", "Open or close an observed native dropdown",
            "Sets the owned dropdown checkbox and sends one uniquely registered ButtonClick to the owning dropdown component. " +
            "No option selection. Guard with expectedOpen/expectedAddonId; dryRun validates without input. Pending transitions " +
            "are refused. Re-query isOpen/pending and visible list before selecting an option.", openSchema, args =>
            {
                var desired = CheckboxTools.StrictBoolean(args, "open");
                var expected = args["expectedOpen"] == null ? (bool?)null : CheckboxTools.StrictBoolean(args, "expectedOpen");
                var target = Resolve(args);
                var result = OpenAt(target.Node, desired, expected, CheckboxTools.StrictBoolean(args, "dryRun", false));
                return Finish(result, target, args);
            }, mutating: true);
        var set = Common();
        set["properties"]!["index"] = new JObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 1023 };
        set["properties"]!["expectedLabel"] = new JObject { ["type"] = "string", ["description"] = "Required exact current label at index, from get_addon_dropdown." };
        set["properties"]!["expectedIndex"] = new JObject { ["type"] = "integer", ["minimum"] = -1, ["maximum"] = 1023 };
        set["properties"]!["dryRun"] = new JObject { ["type"] = "boolean" };
        ((JArray)set["required"]!).Add("index"); ((JArray)set["required"]!).Add("expectedLabel");
        registry.Add("select_addon_dropdown_item", "Select an observed native dropdown option",
            "Requires an open dropdown: select one indexed option through SDK SelectItem, then send one SDK list-item event to its uniquely registered owning dropdown. Requires an exact " +
            "expectedLabel and optionally expectedIndex/expectedAddonId. Refuses disabled items, pending UI changes and invalid " +
            "ownership. dryRun validates without selecting; identical selections send no input. Native selection readback does " +
            "not prove surrounding addon behavior: verify the actual resulting map/settings/UI separately. No guessed callbacks.", set, args =>
            {
                var index = MapTools.Integer(args, "index", -1, 0, 1023);
                if (index < 0 || args["expectedLabel"]?.Type != JTokenType.String) throw new ToolException("index and string expectedLabel are required");
                var expected = args["expectedIndex"] == null ? (int?)null : MapTools.Integer(args, "expectedIndex", -1, -1, 1023);
                var target = Resolve(args);
                var result = SelectAt(target.Node, index, args.Value<string>("expectedLabel")!, expected, CheckboxTools.StrictBoolean(args, "dryRun", false));
                return Finish(result, target, args);
            }, mutating: true);
    }

    private static JObject Finish(JObject result, (string Name, uint Id, nint Node, nint Unit) target, JObject args)
    {
        result["addon"] = target.Name; result["addonId"] = target.Id; result["nodeId"] = args["nodeId"]!.DeepClone();
        var unit = UiTools.ResolveAddon(target.Name, null);
        var copy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if ((nint)unit != target.Unit || !copy.HasValue || copy.Value.Id != target.Id || !copy.Value.IsVisible || !copy.Value.IsReady ||
            (nint)AddonProbeTools.FindNode(unit, args.Value<uint>("nodeId"), componentPath: AddonProbeTools.ReadComponentPath(args)) != target.Node)
        {
            result["verified"] = false; result["exactMatch"] = false;
            result["reason"] = "addon/control identity changed after input; re-query resulting UI";
        }
        return result;
    }

    private static (string Name, uint Id, nint Node, nint Unit) Resolve(JObject args)
    {
        if (args["addon"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>("addon"))) throw new ToolException("exact addon is required");
        if (args["nodeId"]?.Type != JTokenType.Integer || !uint.TryParse(args["nodeId"]!.ToString(), out var id)) throw new ToolException("nodeId must be an unsigned integer");
        if (args["componentId"] != null) throw new ToolException("use componentPath, not componentId");
        var path = AddonProbeTools.ReadComponentPath(args);
        var name = args.Value<string>("addon")!;
        var unit = UiTools.ResolveAddon(name, null);
        var copy = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!copy.HasValue || !copy.Value.IsVisible || !copy.Value.IsReady || unit->NameString != name) throw new ToolException("exact addon is hidden, unreadable or not ready");
        if (args["expectedAddonId"] != null && (args["expectedAddonId"]!.Type != JTokenType.Integer || !uint.TryParse(args["expectedAddonId"]!.ToString(), out var expected) || expected != copy.Value.Id)) throw new ToolException("expectedAddonId no longer matches or is invalid");
        if (UiTools.ScopeVisibility(unit, path) != true) throw new ToolException("dropdown scope is hidden or unknown");
        var node = AddonProbeTools.FindNode(unit, id, componentPath: path);
        if (NativeUiVisibility.Read((nint)node) != true) throw new ToolException("dropdown is hidden or has unknown visibility");
        return (name, copy.Value.Id, (nint)node, (nint)unit);
    }

    internal static bool TryRead(nint node, out nint component, out nint list, out int count, out int selected, out bool open, out bool pending)
    {
        component = list = 0; count = 0; selected = -1; open = pending = false;
        var owner = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)node, out _);
        if (!owner.HasValue || (int)owner.Value.Type < 1000 || UiTools.ComponentKindOf((AtkComponentNode*)node) != ComponentType.DropDownList) return false;
        var control = MemoryProbe.TryReadStruct<AtkComponentDropDownList>((ulong)owner.Value.Component, out _);
        if (!control.HasValue || (nint)control.Value.OwnerNode != node) return false;
        var items = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)control.Value.List, out _);
        if (!items.HasValue || items.Value.ListLength is < 0 or > 1024 || items.Value.SelectedItemIndex < -1 || items.Value.SelectedItemIndex >= items.Value.ListLength) return false;
        var listNode = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)items.Value.OwnerNode, out _);
        if (!listNode.HasValue || (nint)listNode.Value.ParentNode != node || listNode.Value.Component != (AtkComponentBase*)control.Value.List ||
            CheckboxTools.Find(control.Value.UldManager, listNode.Value.NodeId) != (AtkResNode*)items.Value.OwnerNode ||
            UiTools.ComponentKindOf(items.Value.OwnerNode) != ComponentType.List) return false;
        component = (nint)owner.Value.Component; list = (nint)control.Value.List; count = items.Value.ListLength;
        selected = items.Value.SelectedItemIndex; open = control.Value.IsOpen; pending = control.Value.OpenStateChangePending;
        return true;
    }

    internal static string? Option(nint list, int index)
    {
        var items = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)list, out _);
        if (!items.HasValue || index < 0 || index >= items.Value.ListLength || items.Value.ListLength > 1024 || items.Value.ItemLabels == null) return null;
        var pointer = MemoryProbe.TryReadStruct<nint>((ulong)items.Value.ItemLabels + (ulong)index * 8, out _);
        return pointer.HasValue ? MapTools.LabelAt(pointer.Value) : null;
    }

    internal static JObject ReadAt(nint node, int offset, int max)
    {
        if (offset < 0 || offset > 1024 || max < 1 || max > 100) throw new ToolException("invalid dropdown option page");
        if (!TryRead(node, out _, out var list, out var count, out var selected, out var open, out var pending)) throw new ToolException("invalid or unsupported dropdown ownership/state");
        var options = new JArray(); var incomplete = false;
        var end = Math.Min(count, offset + max);
        for (var i = offset; i < end; i++) { var label = Option(list, i); incomplete |= label == null; options.Add(new JObject { ["index"] = i, ["label"] = label }); }
        var selectedLabel = selected < 0 ? null : Option(list, selected);
        incomplete |= selected >= 0 && selectedLabel == null;
        return new JObject { ["selectedIndex"] = selected, ["selectedLabel"] = selectedLabel,
            ["isOpen"] = open, ["openStateChangePending"] = pending, ["optionCount"] = count, ["offset"] = offset,
            ["count"] = options.Count, ["nextOffset"] = end < count ? end : null, ["truncated"] = end < count,
            ["scanIncomplete"] = incomplete, ["options"] = options };
    }

    internal static JObject SelectAt(nint node, int index, string label, int? expected, bool dryRun)
    {
        if (!TryRead(node, out var control, out var list, out var count, out var before, out var isOpen, out var pending)) throw new ToolException("invalid or unsupported dropdown ownership/state");
        var owner = MemoryProbe.TryReadStruct<AtkResNode>((ulong)node, out _);
        if (!owner.HasValue || NativeUiVisibility.Read(node) != true || (owner.Value.NodeFlags & NodeFlags.Enabled) == 0) throw new ToolException("dropdown is hidden, disabled or unknown");
        if (pending) throw new ToolException("dropdown open-state change is pending; re-query after the next frame");
        if (!isOpen) throw new ToolException("dropdown is closed; use set_addon_dropdown_open and re-query after the transition");
        var listState = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)list, out _);
        var listOwner = listState.HasValue ? MemoryProbe.TryReadStruct<AtkResNode>((ulong)listState.Value.OwnerNode, out _) : null;
        if (!listOwner.HasValue || NativeUiVisibility.Read((nint)listState!.Value.OwnerNode) != true || (listOwner.Value.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("dropdown list is hidden, disabled or unknown");
        if (index < 0 || index >= count || Option(list, index) != label) throw new ToolException("index/expectedLabel no longer matches a readable option");
        if (expected.HasValue && expected.Value != before) throw new ToolException("expectedIndex no longer matches");
        var data = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)list, out _);
        if (!data.HasValue) throw new ToolException("dropdown list became unreadable");
        var table = MemoryProbe.TryReadStruct<AtkComponentList.AtkComponentListVirtualTable>((ulong)data.Value.VirtualTable, out _);
        if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.GetItemDisabledState)?.Executable != true) throw new ToolException("SDK list disabled-state accessor is unresolved or not executable");
        if (((AtkComponentList*)list)->GetItemDisabledState(index)) throw new ToolException("dropdown option is disabled");
        var dispatched = !dryRun && before != index;
        ValidateNotification(control, list);
        var notified = false;
        if (dispatched)
        {
            if (MemoryProbe.Query((ulong)(nint)AtkComponentDropDownList.MemberFunctionPointers.SelectItem)?.Executable != true) throw new ToolException("SDK dropdown SelectItem is unresolved or not executable");
            if (MemoryProbe.Query((ulong)(nint)AtkComponentList.MemberFunctionPointers.DispatchItemEvent)?.Executable != true) throw new ToolException("SDK list DispatchItemEvent is unresolved or not executable");
            ((AtkComponentDropDownList*)control)->SelectItem(index);
            if (TryRead(node, out var selectedControl, out var selectedList, out _, out var selectedIndex, out _, out _) && selectedControl == control && selectedList == list && selectedIndex == index && Option(list, index) == label)
            {
                ValidateNotification(control, list);
                ((AtkComponentList*)list)->DispatchItemEvent(index, AtkEventType.ListItemClick);
                notified = true;
            }
        }
        var valid = TryRead(node, out var afterControl, out var afterList, out _, out var actual, out var open, out _) && afterControl == control && afterList == list;
        var actualLabel = valid && actual >= 0 ? Option(list, actual) : null;
        return new JObject { ["dryRun"] = dryRun, ["before"] = before, ["selectedIndex"] = valid ? actual : null,
            ["selectedLabel"] = actualLabel, ["isOpen"] = valid ? open : null,
            ["dispatched"] = dispatched, ["notificationDispatched"] = notified,
            ["verified"] = valid, ["exactMatch"] = valid && actual == index && actualLabel == label };
    }

    internal static void ValidateNotification(nint control, nint list)
    {
        var items = MemoryProbe.TryReadStruct<AtkComponentList>((ulong)list, out _);
        if (!items.HasValue) throw new ToolException("dropdown list is unreadable");
        var owner = MemoryProbe.TryReadStruct<AtkResNode>((ulong)items.Value.OwnerNode, out _);
        if (!owner.HasValue) throw new ToolException("dropdown list owner is unreadable");
        var seen = new HashSet<nint>(); var matches = 0;
        var next = owner.Value.AtkEventManager.Event;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("dropdown event chain is cyclic or oversized");
            var evt = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!evt.HasValue) throw new ToolException("dropdown event is unreadable");
            if (evt.Value.State.EventType == AtkEventType.ListItemClick)
            {
                if ((nint)evt.Value.Listener != control || ++matches > 1) throw new ToolException("dropdown list notification has a foreign or ambiguous listener");
            }
            next = evt.Value.NextEvent;
        }
        if (matches != 1) throw new ToolException("no unique ListItemClick notification to the owning dropdown");
        var receiver = MemoryProbe.TryReadStruct<AtkEventListener>((ulong)control, out _);
        var table = receiver.HasValue ? MemoryProbe.TryReadStruct<AtkEventListener.AtkEventListenerVirtualTable>((ulong)receiver.Value.VirtualTable, out _) : null;
        if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.ReceiveEvent)?.Executable != true) throw new ToolException("dropdown notification receiver is unresolved or not executable");
    }

    internal static JObject OpenAt(nint node, bool desired, bool? expected, bool dryRun)
    {
        if (!TryRead(node, out var control, out var list, out _, out _, out var before, out var pending)) throw new ToolException("invalid or unsupported dropdown ownership/state");
        var owner = MemoryProbe.TryReadStruct<AtkResNode>((ulong)node, out _);
        if (!owner.HasValue || NativeUiVisibility.Read(node) != true || (owner.Value.NodeFlags & NodeFlags.Enabled) == 0) throw new ToolException("dropdown is hidden, disabled or unknown");
        if (pending) throw new ToolException("dropdown transition is pending; re-query after the next frame");
        if (expected.HasValue && before != expected.Value) throw new ToolException("expectedOpen no longer matches");
        var dropdown = MemoryProbe.TryReadStruct<AtkComponentDropDownList>((ulong)control, out _);
        var checkbox = dropdown.HasValue ? MemoryProbe.TryReadStruct<AtkComponentCheckBox>((ulong)dropdown.Value.Checkbox, out _) : null;
        if (!checkbox.HasValue) throw new ToolException("dropdown checkbox is unreadable");
        var checkboxNode = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)checkbox.Value.OwnerNode, out _);
        if (!checkboxNode.HasValue || (nint)checkboxNode.Value.ParentNode != node ||
            checkboxNode.Value.Component != (AtkComponentBase*)dropdown!.Value.Checkbox ||
            CheckboxTools.Find(dropdown.Value.UldManager, checkboxNode.Value.NodeId) != (AtkResNode*)checkbox.Value.OwnerNode ||
            !CheckboxTools.TryReadCheckbox((nint)checkbox.Value.OwnerNode, out var checkControl, out var checkedBefore) || checkControl != (nint)dropdown.Value.Checkbox)
            throw new ToolException("dropdown checkbox ownership is invalid");
        if (NativeUiVisibility.Read((nint)checkbox.Value.OwnerNode) != true || (checkboxNode.Value.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("dropdown checkbox is hidden, disabled or unknown");
        ValidateNotification(control, list);
        var next = checkboxNode.Value.AtkEventManager.Event;
        var seen = new HashSet<nint>(); uint? parameter = null;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("dropdown checkbox event chain is cyclic or oversized");
            var evt = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!evt.HasValue) throw new ToolException("dropdown checkbox event is unreadable");
            if (evt.Value.State.EventType == AtkEventType.ButtonClick)
            {
                if ((nint)evt.Value.Listener != control || parameter.HasValue) throw new ToolException("dropdown checkbox listener is foreign or ambiguous");
                parameter = evt.Value.Param;
            }
            next = evt.Value.NextEvent;
        }
        if (!parameter.HasValue) throw new ToolException("dropdown checkbox has no unique ButtonClick notification");
        var dispatched = !dryRun && before != desired;
        if (dispatched)
        {
            if (checkedBefore != desired)
            {
                if (MemoryProbe.Query((ulong)(nint)AtkComponentButton.MemberFunctionPointers.SetChecked)?.Executable != true) throw new ToolException("SDK checkbox SetChecked is unresolved or not executable");
                ((AtkComponentCheckBox*)checkControl)->SetChecked(desired);
            }
            if (!CheckboxTools.TryReadCheckbox((nint)checkbox.Value.OwnerNode, out var actualControl, out var actualChecked) || actualControl != checkControl || actualChecked != desired)
                throw new ToolException("dropdown checkbox did not reach desired state; notification not sent");
            byte* eventBytes = stackalloc byte[64]; byte* dataBytes = stackalloc byte[64];
            new Span<byte>(eventBytes, 64).Clear(); new Span<byte>(dataBytes, 64).Clear();
            var evt = (AtkEvent*)eventBytes;
            evt->Node = (AtkResNode*)checkbox.Value.OwnerNode; evt->Target = (AtkEventTarget*)checkbox.Value.OwnerNode;
            evt->Listener = (AtkEventListener*)control; evt->Param = parameter.Value; evt->State.EventType = AtkEventType.ButtonClick;
            ((AtkEventListener*)control)->ReceiveEvent(AtkEventType.ButtonClick, unchecked((int)parameter.Value), evt, (AtkEventData*)dataBytes);
        }
        var valid = TryRead(node, out var afterControl, out var afterList, out _, out _, out var open, out var afterPending) && afterControl == control && afterList == list;
        return new JObject { ["dryRun"] = dryRun, ["before"] = before, ["isOpen"] = valid ? open : null,
            ["openStateChangePending"] = valid ? afterPending : null, ["dispatched"] = dispatched,
            ["verified"] = valid, ["exactMatch"] = valid && !afterPending && open == desired };
    }
}
