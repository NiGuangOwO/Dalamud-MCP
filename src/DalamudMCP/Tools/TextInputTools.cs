using System;
using System.Collections.Generic;
using System.Text;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>One bounded native text edit; never submits the surrounding form.</summary>
internal static unsafe class TextInputTools
{
    private const int MaxBytes = 256;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static void Register(ToolRegistry registry) => registry.Add(
        "set_addon_text_input", "Set a native addon text input",
        "Replaces text in one visible, enabled native TextInput using the installed SDK SetText path, " +
        "which applies the control's input limits/formatting. Not Dear ImGui. Discover nodeId/componentPath " +
        "with list_addon_elements. Plain text only, at most 256 UTF-8 bytes; empty clears. dryRun reads " +
        "without editing. Optional expectedText and expectedAddonId guard prior state. Returns the actual " +
        "evaluated text and exactMatch because native filtering/truncation can change it. Does not press " +
        "Enter, submit chat, activate a search button or synthesize an addon callback. Verify resulting UI separately.",
        Json.Schema(
            ("addon", "string", "Exact loaded native addon name.", true),
            ("nodeId", "integer", "TextInput node id from list_addon_elements.", true),
            ("text", "string", "Plain text, at most 256 UTF-8 bytes. Empty clears; controls and invalid Unicode are refused.", true),
            ("componentPath", "array of integer", "Exact component path, outermost first (0-40 ids). Empty/omitted means top-level only.", false),
            ("expectedText", "string", "Optional exact guard on current evaluated text.", false),
            ("expectedAddonId", "integer", "Optional loaded addon id guard; re-discover after replacement.", false),
            ("dryRun", "boolean", "Read current text and report the intended edit without applying it.", false)),
        Set, mutating: true);

    internal static string PlainText(JObject args, string key)
    {
        if (args[key]?.Type != JTokenType.String) throw new ToolException(key + " must be a JSON string");
        var text = args.Value<string>(key)!;
        if (text.Length > MaxBytes) throw new ToolException(key + " exceeds 256 UTF-8 bytes");
        foreach (var c in text)
            if (char.IsControl(c) || c is '\u2028' or '\u2029')
                throw new ToolException(key + " must be single-line plain text without control characters");
        try
        {
            if (Utf8.GetByteCount(text) > MaxBytes) throw new ToolException(key + " exceeds 256 UTF-8 bytes");
        }
        catch (EncoderFallbackException) { throw new ToolException(key + " contains invalid Unicode"); }
        return text;
    }

    internal static bool TryReadInput(nint address, nint addon, out nint component, out string? text)
    {
        component = 0; text = null;
        var node = MemoryProbe.TryReadStruct<AtkComponentNode>((ulong)address, out _);
        if (!node.HasValue || (int)node.Value.Type < 1000 ||
            UiTools.ComponentKindOf((AtkComponentNode*)address) != ComponentType.TextInput) return false;
        var input = MemoryProbe.TryReadStruct<AtkComponentTextInput>((ulong)node.Value.Component, out _);
        if (!input.HasValue || (nint)input.Value.OwnerNode != address || (nint)input.Value.OwnerAddon != addon)
            return false;
        var bytes = MemoryProbe.TryReadBytes((ulong)(byte*)input.Value.EvaluatedString.StringPtr, 4097, out _);
        if (bytes == null) return false;
        var end = Array.IndexOf(bytes, (byte)0);
        if (end < 0) return false;
        try { text = Utf8.GetString(bytes, 0, end); }
        catch (DecoderFallbackException) { return false; }
        component = (nint)node.Value.Component;
        return true;
    }

    internal static bool IsUsable(nint address)
    {
        var visited = new HashSet<nint>();
        var current = address;
        for (var depth = 0; current != 0; depth++)
        {
            if (depth >= 64 || !visited.Add(current)) return false;
            var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)current, out _);
            if (!node.HasValue || (node.Value.NodeFlags & NodeFlags.Visible) == 0 ||
                (depth == 0 && (node.Value.NodeFlags & NodeFlags.Enabled) == 0)) return false;
            current = (nint)node.Value.ParentNode;
        }
        return address != 0;
    }

    private static uint Unsigned(JObject args, string key)
    {
        if (args[key]?.Type != JTokenType.Integer || !uint.TryParse(args[key]!.ToString(), out var value))
            throw new ToolException(key + " must be an unsigned integer");
        return value;
    }

    private static object Set(JObject args)
    {
        var desired = PlainText(args, "text");
        var expected = args["expectedText"] == null ? null : PlainText(args, "expectedText");
        var dryRun = CheckboxTools.StrictBoolean(args, "dryRun", false);
        var id = Unsigned(args, "nodeId");
        var expectedId = args["expectedAddonId"] == null ? (uint?)null : Unsigned(args, "expectedAddonId");
        if (args["addon"]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(args.Value<string>("addon")))
            throw new ToolException("addon must be an exact loaded addon name");
        if (args["componentId"] != null) throw new ToolException("use componentPath, not componentId");
        var path = AddonProbeTools.ReadComponentPath(args);
        var name = args.Value<string>("addon")!;
        var unit = UiTools.ResolveAddon(name, null);
        var owner = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)unit, out _);
        if (!owner.HasValue || !unit->IsReady || !unit->IsVisible || unit->NameString != name)
            throw new ToolException("exact addon is unavailable, not ready or hidden");
        var addonId = unit->Id;
        if (expectedId.HasValue && expectedId.Value != addonId) throw new ToolException("expectedAddonId no longer matches");
        var node = AddonProbeTools.FindNode(unit, id, componentPath: path);
        if (!TryReadInput((nint)node, (nint)unit, out var component, out var before) || !IsUsable((nint)node))
            throw new ToolException("TextInput is unreadable, ambiguous, hidden, disabled or has mismatched ownership");
        if (expected != null && expected != before) throw new ToolException("expectedText no longer matches");
        var input = (AtkComponentTextInput*)component;
        var enabled = MemoryProbe.TryReadStruct<AtkComponentTextInput>((ulong)component, out _);
        if (!enabled.HasValue || !enabled.Value.Enabled) throw new ToolException("native text input is disabled");
        var dispatched = !dryRun && before != desired;
        var actual = before;
        if (dispatched)
        {
            if (MemoryProbe.Query((ulong)(nint)AtkComponentTextInput.MemberFunctionPointers.SetText)?.Executable != true)
                throw new ToolException("installed SDK SetText function is unresolved or not executable");
            input->SetText(desired);
            // SetText may notify UI/plugin listeners. Re-resolve before the result read.
            var afterUnit = UiTools.ResolveAddon(name, null);
            if (afterUnit != unit || afterUnit->Id != addonId ||
                AddonProbeTools.FindNode(afterUnit, id, componentPath: path) != node ||
                !TryReadInput((nint)node, (nint)unit, out var afterComponent, out actual) || afterComponent != component)
                return new JObject { ["addon"] = name, ["addonId"] = addonId, ["nodeId"] = id,
                    ["dispatched"] = true, ["verified"] = false, ["reason"] = "input identity changed after SetText; re-query UI" };
        }
        return new JObject { ["addon"] = name, ["addonId"] = addonId, ["nodeId"] = id,
            ["componentPath"] = new JArray(path ?? Array.Empty<uint>()), ["dryRun"] = dryRun,
            ["before"] = before, ["text"] = actual, ["changed"] = actual != before,
            ["dispatched"] = dispatched, ["verified"] = true, ["exactMatch"] = actual == desired };
    }
}
