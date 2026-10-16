using System;
using System.Collections.Generic;

namespace DalamudMCP.Tools;

internal readonly record struct StatusSlot(int Index, ushort Id, ushort Param, float Remaining, ulong Source);

/// <summary>The native array is sparse; its header byte is not a count of occupied entries.</summary>
internal static class StatusSlotReader
{
    public static List<StatusSlot> Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 60 * 16) throw new ArgumentException("expected all 60 native status slots", nameof(bytes));
        var result = new List<StatusSlot>();
        for (var i = 0; i < 60; i++)
        {
            var slot = bytes.Slice(i * 16, 16);
            var id = BitConverter.ToUInt16(slot);
            if (id == 0) continue;
            result.Add(new StatusSlot(i, id, BitConverter.ToUInt16(slot[2..]), BitConverter.ToSingle(slot[4..]), BitConverter.ToUInt64(slot[8..])));
        }
        return result;
    }
}
