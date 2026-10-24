using System;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class NumericInputTools
{
    public static void Register(ToolRegistry registry) => registry.Add(
        "set_addon_numeric_input", "Set one native numeric input",
        "Sets a visible, enabled native NumericInput through the installed SDK SetValue path. Reads the " +
        "control's current value and live Min/Max, refuses out-of-range values, and returns actual readback. " +
        "Discover nodeId/componentPath first; dryRun previews without editing. expectedValue/expectedAddonId " +
        "optionally guard observed state. Identical values send no edit. Does not press Enter, confirm a " +
        "quantity dialog, select a menu entry or synthesize an addon callback. Inspect resulting UI separately.",
        Json.Schema(
            ("addon", "string", "Exact loaded native addon name.", true),
            ("nodeId", "integer", "NumericInput node id from list_addon_elements.", true),
            ("value", "integer", "Desired signed 32-bit integer within the control's current Min/Max.", true),
            ("componentPath", "array of integer", "Exact component scope, outermost first (0-40 ids); empty/omitted is top-level.", false),
            ("expectedValue", "integer", "Optional exact guard on the current native value.", false),
            ("expectedAddonId", "integer", "Optional guard on the observed loaded-addon id.", false),
            ("dryRun", "boolean", "Read current value/limits and validate the proposed value without editing.", false)),
        Set, mutating: true);

    internal static int Integer(JObject args, string key)
    {
        if (args[key]?.Type != JTokenType.Integer || !int.TryParse(args[key]!.ToString(), out var value))
            throw new ToolException(key + " must be a signed 32-bit JSON integer");
        return value;
    }

    private static uint Id(JObject args, string key)
    {
        if (args[key]?.Type != JTokenType.Integer || !uint.TryParse(args[key]!.ToString(), out var value))
            throw new ToolException(key + " must be an unsigned integer");
        return value;
    }

    internal static void CheckRange(int value, int min, int max)
    {
        if (min > max) throw new ToolException("native numeric input has invalid limits");
        if (value < min || value > max) throw new ToolException($"value must be within the native range {min}..{max}");
    }

    internal static bool TryRead(nint address, nint addon, out nint component, out int value, out int min, out int max)
    {
        component = 0; value = min = max = 0;
        var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)address, out _);
        if (!node.HasValue || (int)node.Value.Type < 1000 ||
            UiTools.ComponentKindOf((AtkComponentNode*)address) != ComponentType.NumericInput) return false;
        var input = MemoryProbe.TryReadStruct<AtkComponentNumericInput>((ulong)node.Value.Component, out _);
        if (!input.HasValue || (nint)input.Value.OwnerNode != address || (nint)input.Value.OwnerAddon != addon ||
            input.Value.Data.Min > input.Value.Data.Max) return false;
        component = (nint)node.Value.Component;
        value = input.Value.Value; min = input.Value.Data.Min; max = input.Value.Data.Max;
        return true;
    }

    private static object Set(JObject args)
    {
        var desired = Integer(args, "value");
        var expected = args["expectedValue"] == null ? (int?)null : Integer(args, "expectedValue");
        var expectedId = args["expectedAddonId"] == null ? (uint?)null : Id(args, "expectedAddonId");
        var id = Id(args, "nodeId");
        var dryRun = CheckboxTools.StrictBoolean(args, "dryRun", false);
        if (args["addon"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>("addon")))
            throw new ToolException("addon must be an exact loaded addon name");
        if (args["componentId"] != null) throw new ToolException("use componentPath, not componentId");
        var path = AddonProbeTools.ReadComponentPath(args);
        var name = args.Value<string>("addon")!;
        var unit = UiTools.ResolveAddon(name, null);
        if (!MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _).HasValue ||
            !unit->IsVisible || !unit->IsReady || unit->NameString != name)
            throw new ToolException("exact addon is unavailable, hidden or not ready");
        var addonId = unit->Id;
        if (expectedId.HasValue && expectedId.Value != addonId) throw new ToolException("expectedAddonId no longer matches");
        if (UiTools.ScopeVisibility(unit, path) != true) throw new ToolException("component scope is hidden or unknown");
        var node = AddonProbeTools.FindNode(unit, id, componentPath: path);
        if (!TryRead((nint)node, (nint)unit, out var component, out var before, out var min, out var max))
            throw new ToolException("NumericInput is unreadable, ambiguous, has invalid limits or mismatched ownership");
        var state = MemoryProbe.TryReadStruct<AtkResNode>((ulong)node, out _);
        if (!state.HasValue || NativeUiVisibility.Read((nint)node) != true || (state.Value.NodeFlags & NodeFlags.Enabled) == 0)
            throw new ToolException("NumericInput is hidden, disabled or has unknown visibility");
        if (expected.HasValue && before != expected.Value) throw new ToolException("expectedValue no longer matches");
        CheckRange(desired, min, max);
        var dispatched = !dryRun && before != desired;
        var actual = before;
        if (dispatched)
        {
            if (MemoryProbe.Query((ulong)(nint)AtkComponentNumericInput.MemberFunctionPointers.SetValue)?.Executable != true)
                throw new ToolException("installed SDK SetValue is unresolved or not executable");
            ((AtkComponentNumericInput*)component)->SetValue(desired);
            var afterUnit = UiTools.ResolveAddon(name, null);
            if (afterUnit != unit || afterUnit->Id != addonId || !afterUnit->IsReady ||
                AddonProbeTools.FindNode(afterUnit, id, componentPath: path) != node ||
                !TryRead((nint)node, (nint)unit, out var afterComponent, out actual, out min, out max) || afterComponent != component)
                return new JObject { ["addon"] = name, ["addonId"] = addonId, ["nodeId"] = id,
                    ["dispatched"] = true, ["verified"] = false, ["reason"] = "numeric input identity changed after SetValue; re-query UI" };
        }
        return new JObject { ["addon"] = name, ["addonId"] = addonId, ["nodeId"] = id,
            ["componentPath"] = new JArray(path ?? Array.Empty<uint>()), ["dryRun"] = dryRun,
            ["before"] = before, ["value"] = actual, ["min"] = min, ["max"] = max,
            ["changed"] = before != actual, ["dispatched"] = dispatched, ["verified"] = true,
            ["exactMatch"] = actual == desired };
    }
}
