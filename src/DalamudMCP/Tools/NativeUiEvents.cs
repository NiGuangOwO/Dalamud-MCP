using System.Collections.Generic;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

internal static unsafe class NativeUiEvents
{
    internal static uint Parameter(nint address, AtkEventType type)
    {
        var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)address, out _);
        if (!node.HasValue) throw new ToolException("event node is unreadable");
        var next = node.Value.AtkEventManager.Event;
        var seen = new HashSet<nint>();
        uint? matching = null, fallback = null;
        while (next != null)
        {
            if (seen.Count >= 64 || !seen.Add((nint)next)) throw new ToolException("UI event chain is cyclic or oversized");
            var entry = MemoryProbe.TryReadStruct<AtkEvent>((ulong)next, out _);
            if (!entry.HasValue) throw new ToolException("UI event is unreadable");
            if (entry.Value.State.EventType == type)
            {
                if (matching.HasValue && matching.Value != entry.Value.Param) throw new ToolException("requested UI event has ambiguous registered parameters");
                matching = entry.Value.Param; // Zero is a real registered parameter.
            }
            if (!fallback.HasValue && entry.Value.Param != 0) fallback = entry.Value.Param;
            next = entry.Value.NextEvent;
        }
        // Retain historical fallback only when this event type is not registered.
        return matching ?? fallback ?? uint.MaxValue;
    }
}
