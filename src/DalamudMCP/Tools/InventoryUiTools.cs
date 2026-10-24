using System;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>The inventory agent is persistent; its activation is not the bag window's open state.</summary>
internal static unsafe class InventoryUiTools
{
    private static nint Resolve()
    {
        try
        {
            var module = UIModule.Instance();
            if (module != null) return (nint)module;
        }
        catch (InvalidOperationException) { }
        throw new ToolException("inventory UI module is unavailable or unresolved");
    }

    private static UIModuleInterface* Validate(nint address)
    {
        var module = MemoryProbe.TryReadStruct<UIModuleInterface>((ulong)address, out _);
        if (!module.HasValue) throw new ToolException("inventory UI module is unreadable");
        var table = MemoryProbe.TryReadStruct<UIModuleInterface.UIModuleInterfaceVirtualTable>((ulong)module.Value.VirtualTable, out _);
        if (!table.HasValue || !Executable((nint)table.Value.IsInventoryOpen))
            throw new ToolException("inventory state function is unresolved or not executable");
        return (UIModuleInterface*)address;
    }

    private static bool Executable(nint address) => MemoryProbe.Query((ulong)address)?.Executable == true;

    internal static bool ReadAt(nint address) => Validate(address)->IsInventoryOpen();

    public static bool IsOpen() => ReadAt(Resolve());

    public static object Set(bool open) => SetAt(Resolve(), open);

    internal static object SetAt(nint address, bool open)
    {
        var module = Validate(address);
        var before = module->IsInventoryOpen();
        var dispatched = before != open;
        if (dispatched)
        {
            var table = MemoryProbe.TryReadStruct<UIModuleInterface.UIModuleInterfaceVirtualTable>((ulong)module->VirtualTable, out _);
            if (!table.HasValue || !Executable(open ? (nint)table.Value.OpenInventory : (nint)table.Value.CloseInventory))
                throw new ToolException("inventory open/close function is unresolved or not executable");
            if (open) module->OpenInventory(0); // SDK documents 0 = inventory, 1 = key items.
            else module->CloseInventory();
        }
        var after = ReadAt(address);
        return new JObject
        {
            ["addon"] = "inventory", ["agentId"] = 6, ["requested"] = true,
            ["dispatched"] = dispatched, ["beforeOpen"] = before, ["open"] = after,
            [open ? "opened" : "closed"] = after == open,
            ["observed"] = after == open, ["observation"] = "immediate",
            ["via"] = open ? "UIModule.OpenInventory" : "UIModule.CloseInventory",
        };
    }
}
