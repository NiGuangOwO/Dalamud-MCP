using System;
using System.Collections;
using System.Reflection;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class AddonDispatchTools
{
    public static void Register(ToolRegistry registry) => registry.Add(
        "get_addon_native_dispatch", "Inspect original addon dispatch addresses",
        "Reads current and original ReceiveEvent addresses for one loaded addon. Dalamud can replace entries with " +
        "managed lifecycle thunks, which are not game executable RVAs. Uses bounded shape-checked reflection over " +
        "the installed lifecycle registry and guarded pointer reads. No hooks, native calls or memory writes. " +
        "Unsupported SDK layouts return unavailable.",
        Json.Schema(("addon", "string", "Exact internal name from list_loaded_addons", true)), Inspect);

    private static object Inspect(JObject args)
    {
        var name = args.Value<string>("addon");
        if (string.IsNullOrWhiteSpace(name)) throw new ToolException("missing required parameter: addon");
        var unit = UiTools.ResolveAddon(name, null);
        if (unit == null || !unit->IsReady || unit->NameString != name) return Unavailable(name, "addon is not loaded and ready");
        var current = (nint)unit->VirtualTable;
        try
        {
            var type = typeof(IDalamudPluginInterface).Assembly.GetType("Dalamud.Game.Addon.Lifecycle.AddonLifecycle");
            var field = type?.GetField("AllocatedTables", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            if (field?.GetValue(null) is not IEnumerable entries) return Unavailable(name, "installed lifecycle registry is unsupported");
            var original = FindOriginal(entries, (nint)unit, current);
            if (original == 0) return Unavailable(name, "matching original lifecycle table is unavailable");
            var oldHandler = MemoryProbe.TryReadStruct<ulong>((ulong)original + 16, out var error);
            var currentHandler = MemoryProbe.TryReadStruct<ulong>((ulong)current + 16, out _);
            if (!oldHandler.HasValue || !currentHandler.HasValue) return Unavailable(name, error ?? "vtable is unreadable");
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var module = process.MainModule;
            var moduleBase = module == null ? 0UL : (ulong)module.BaseAddress;
            var inModule = module != null && oldHandler.Value >= moduleBase && oldHandler.Value - moduleBase < (ulong)module.ModuleMemorySize;
            return new JObject
            {
                ["available"] = true, ["addon"] = name, ["addonId"] = (int)unit->Id,
                ["address"] = $"0x{(nuint)unit:X}", ["currentVirtualTable"] = $"0x{(nuint)current:X}",
                ["originalVirtualTable"] = $"0x{(nuint)original:X}",
                ["currentReceiveEvent"] = $"0x{currentHandler.Value:X}", ["originalReceiveEvent"] = $"0x{oldHandler.Value:X}",
                ["originalInGameModule"] = inModule, ["gameModuleBase"] = $"0x{moduleBase:X}",
                ["receiveEventRva"] = inModule ? $"0x{oldHandler.Value - moduleBase:X}" : null,
            };
        }
        catch (Exception ex) { return Unavailable(name, $"lifecycle inspection unavailable ({ex.GetType().Name})"); }
    }

    private static JObject Unavailable(string name, string reason) => new()
    { ["available"] = false, ["addon"] = name, ["reason"] = reason };

    private static nint FindOriginal(IEnumerable entries, nint addon, nint modified)
    {
        var count = 0;
        foreach (var entry in entries)
        {
            if (++count > 1024) return 0;
            if (entry == null) continue;
            var type = entry.GetType();
            if (type.FullName != "Dalamud.Game.Addon.Lifecycle.AddonVirtualTable") continue;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var owner = type.GetField("atkUnitBase", flags);
            var changed = type.GetField("<ModifiedVirtualTable>k__BackingField", flags);
            var original = type.GetField("<OriginalVirtualTable>k__BackingField", flags);
            if (owner?.FieldType != typeof(AtkUnitBase*) || changed?.FieldType != typeof(AtkUnitBase.AtkUnitBaseVirtualTable*) ||
                original?.FieldType != typeof(AtkUnitBase.AtkUnitBaseVirtualTable*)) continue;
            if ((nint)Pointer.Unbox(owner.GetValue(entry)!) != addon || (nint)Pointer.Unbox(changed.GetValue(entry)!) != modified) continue;
            return (nint)Pointer.Unbox(original.GetValue(entry)!);
        }
        return 0;
    }
}
