using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>One registered right-click event, without drag or menu selection.</summary>
internal static unsafe class ContextMenuTools
{
    private static readonly int InterfaceOffset = (int)Marshal.OffsetOf<AtkComponentDragDrop>(nameof(AtkComponentDragDrop.AtkDragDropInterface));
    private static readonly string[] SupportedAddons = { "InventoryGrid", "InventoryGrid0", "InventoryGrid1", "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E" };
    internal static bool Supports(string name) => Array.IndexOf(SupportedAddons, name) >= 0;

    public static void Register(ToolRegistry registry)
    {
        registry.Add("get_inventory_context", "Read the open inventory item's context",
            "Reads the live inventory context agent's owning addon, container, slot and item identity only while " +
            "its ContextMenu is visibly open. Validates the target InventoryItem against the context's container/slot. " +
            "Unknown/dummy/unreadable targets remain unavailable. No menu selection or item operation. Match the " +
            "returned container/slot with get_inventory before selecting an entry; nativeItemId/nativeFlags are " +
            "the raw SDK fields, not Dalamud's quality-encoded item id.", Json.Schema(), _ => Read());
        var schema = Json.Schema(
            ("addon", "string", "Exact loaded ordinary carried-bag grid name; see enum.", true),
            ("nodeId", "integer", "DragDrop component node id from list_addon_elements.", true),
            ("componentPath", "array of integer", "Exact component scope, outermost first (0-40 ids); omitted/empty is top-level.", false),
            ("expectedAddonId", "integer", "Optional guard on the observed loaded-addon id.", false),
            ("dryRun", "boolean", "Validate the native control/event without sending input.", false));
        schema["properties"]!["addon"]!["enum"] = new JArray(SupportedAddons);
        registry.Add(
        "open_addon_context_menu", "Request a native DragDrop context menu",
        "Sends one right-button DragDropClick through the loaded addon's uniquely registered event, using " +
        "the installed SDK drag/drop event-data layout. Only visible, enabled DragDrop controls owned by the " +
        "resolved node in an allowlisted ordinary carried-bag grid are supported. Discover nodeId/componentPath first. No drag, item use, left click, " +
        "menu entry selection or callback guessing. dryRun validates and reports the event without input. " +
        "requested/dispatched do not prove a menu appeared: inspect ContextMenu and its actual item afterward.",
        schema,
        Open, mutating: true);
    }

    private static object Read()
    {
        var menu = UiTools.ResolveAddon("ContextMenu", null);
        if (menu == null || !menu->IsVisible || !menu->IsReady)
            return new JObject { ["available"] = false, ["reason"] = "no visible ready ContextMenu" };
        try { return ReadContextAt((nint)AgentInventoryContext.Instance(), menu->Id); }
        catch (InvalidOperationException) { return new JObject { ["available"] = false, ["reason"] = "inventory context agent unresolved" }; }
    }

    internal static object ReadContextAt(nint address, uint menuId)
    {
        var context = MemoryProbe.TryReadStruct<AgentInventoryContext>((ulong)address, out _);
        if (!context.HasValue || context.Value.AddonId != menuId)
            return new JObject { ["available"] = false, ["reason"] = "visible menu does not match a readable inventory context agent" };
        var item = MemoryProbe.TryReadStruct<InventoryItem>((ulong)context.Value.TargetInventorySlot, out _);
        if (!item.HasValue || item.Value.IsSymbolic || item.Value.Container != context.Value.TargetInventoryId ||
            item.Value.Slot != context.Value.TargetInventorySlotId || item.Value.ItemId == 0 || item.Value.Quantity == 0)
            return new JObject { ["available"] = false, ["reason"] = "target is dummy, unreadable, empty or no longer matches the inventory context" };
        return new JObject { ["available"] = true, ["menuAddonId"] = menuId,
            ["ownerAddonId"] = context.Value.OwnerAddonId, ["inventory"] = context.Value.TargetInventoryId.ToString(),
            ["slot"] = context.Value.TargetInventorySlotId, ["nativeItemId"] = item.Value.ItemId,
            ["quantity"] = item.Value.Quantity, ["nativeFlags"] = item.Value.Flags.ToString(),
            ["contextFlags"] = context.Value.TargetInventoryFlags.ToString() };
    }

    private static uint Id(JObject args, string key)
    {
        if (args[key]?.Type != JTokenType.Integer || !uint.TryParse(args[key]!.ToString(), out var id))
            throw new ToolException(key + " must be an unsigned integer");
        return id;
    }

    private static object Open(JObject args)
    {
        var dryRun = CheckboxTools.StrictBoolean(args, "dryRun", false);
        var id = Id(args, "nodeId");
        var expected = args["expectedAddonId"] == null ? (uint?)null : Id(args, "expectedAddonId");
        if (args["addon"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>("addon")))
            throw new ToolException("addon must be an exact loaded addon name");
        if (args["componentId"] != null) throw new ToolException("use componentPath, not componentId");
        var path = AddonProbeTools.ReadComponentPath(args);
        var name = args.Value<string>("addon")!;
        if (!Supports(name)) throw new ToolException("context menus are limited to allowlisted ordinary carried-bag grids");
        var unit = UiTools.ResolveAddon(name, null);
        if (!MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _).HasValue ||
            !unit->IsVisible || !unit->IsReady || unit->NameString != name)
            throw new ToolException("exact addon is unavailable, hidden or not ready");
        if (expected.HasValue && unit->Id != expected.Value) throw new ToolException("expectedAddonId no longer matches");
        if (UiTools.ScopeVisibility(unit, path) != true) throw new ToolException("component scope is hidden or unknown");
        var menu = UiTools.ResolveAddon("ContextMenu", null);
        if (!dryRun && menu != null && menu->IsVisible) throw new ToolException("a ContextMenu is already open; inspect or close it first");
        var node = AddonProbeTools.FindNode(unit, id, componentPath: path);
        var addonId = unit->Id;
        var result = (JObject)DispatchAt((nint)unit, (nint)node, dryRun);
        result["addon"] = name; result["addonId"] = addonId; result["nodeId"] = id;
        result["componentPath"] = new JArray(path ?? Array.Empty<uint>());
        return result;
    }

    internal static object DispatchAt(nint unit, nint address, bool dryRun)
    {
        var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)address, out _);
        if (!node.HasValue || (int)node.Value.Type < 1000 ||
            UiTools.ComponentKindOf((AtkComponentNode*)address) != ComponentType.DragDrop)
            throw new ToolException("node is not a readable DragDrop component");
        var control = MemoryProbe.TryReadStruct<AtkComponentDragDrop>((ulong)node.Value.Component, out _);
        if (!control.HasValue || (nint)control.Value.OwnerNode != address)
            throw new ToolException("DragDrop component ownership is invalid");
        // The interface binds the inner Icon component node, not the outer DragDrop owner.
        var boundNode = control.Value.AtkDragDropInterface.ComponentNode;
        var bound = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)boundNode, out _);
        var icon = MemoryProbe.TryReadStruct<AtkComponentIcon>((ulong)control.Value.AtkComponentIcon, out _);
        if (!bound.HasValue || (int)bound.Value.Type < 1000 || (nint)bound.Value.ParentNode != address ||
            bound.Value.Component != (AtkComponentBase*)control.Value.AtkComponentIcon || !icon.HasValue || icon.Value.OwnerNode != boundNode ||
            CheckboxTools.Find(control.Value.UldManager, bound.Value.NodeId) != (AtkResNode*)boundNode)
            throw new ToolException("DragDrop interface is not bound to its owned inner Icon node");
        if (NativeUiVisibility.Read(address) != true || (node.Value.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("DragDrop control is hidden, disabled or has unknown visibility");
        var seen = new HashSet<nint>();
        var current = node.Value.AtkEventManager.Event;
        uint? parameter = null;
        while (current != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)current)) throw new ToolException("DragDrop event chain is unreadable, cyclic or oversized");
            var evt = MemoryProbe.TryReadStruct<AtkEvent>((ulong)current, out _);
            if (!evt.HasValue) throw new ToolException("DragDrop event is unreadable");
            if (evt.Value.State.EventType == AtkEventType.DragDropClick)
            {
                if ((nint)evt.Value.Listener != unit || parameter.HasValue)
                    throw new ToolException("DragDropClick must have one unambiguous owning-addon listener");
                parameter = evt.Value.Param;
            }
            current = evt.Value.NextEvent;
        }
        if (!parameter.HasValue) throw new ToolException("no registered DragDropClick event");
        var owner = MemoryProbe.TryReadStruct<AtkEventListener>((ulong)unit, out _);
        if (!owner.HasValue) throw new ToolException("addon listener is unreadable");
        var table = MemoryProbe.TryReadStruct<AtkEventListener.AtkEventListenerVirtualTable>((ulong)owner.Value.VirtualTable, out _);
        if (!table.HasValue || MemoryProbe.Query((ulong)(nint)table.Value.ReceiveEvent)?.Executable != true)
            throw new ToolException("addon ReceiveEvent is unresolved or not executable");
        if (!dryRun)
        {
            byte* eventBytes = stackalloc byte[64]; byte* dataBytes = stackalloc byte[64];
            new Span<byte>(eventBytes, 64).Clear(); new Span<byte>(dataBytes, 64).Clear();
            var evt = (AtkEvent*)eventBytes;
            evt->Node = (AtkResNode*)address; evt->Target = (AtkEventTarget*)address;
            evt->Listener = (AtkEventListener*)unit; evt->Param = parameter.Value;
            evt->State.EventType = AtkEventType.DragDropClick;
            var data = (AtkEventData.AtkDragDropData*)dataBytes;
            data->DragDropInterface = (AtkDragDropInterface*)((nint)node.Value.Component + InterfaceOffset);
            data->ComponentNode = boundNode;
            data->MouseButtonId = 1; // Native Atk right button; no modifiers.
            table.Value.ReceiveEvent((AtkEventListener*)unit, AtkEventType.DragDropClick,
                unchecked((int)parameter.Value), evt, (AtkEventData*)dataBytes);
        }
        return new JObject { ["dryRun"] = dryRun, ["requested"] = !dryRun, ["dispatched"] = !dryRun,
            ["event"] = "DragDropClick", ["eventParam"] = parameter.Value, ["mouseButton"] = "right",
            ["dragReferenceIndex"] = control.Value.AtkDragDropInterface.DragDropReferenceIndex };
    }
}
