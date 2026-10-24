using System;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace DalamudMCP.Tools;

internal static unsafe class InventoryContainerRead
{
    public static bool TryRead(ulong address, InventoryType expected, out InventoryContainer container,
        out byte[] bytes, out string reason)
    {
        container = default;
        bytes = Array.Empty<byte>();
        var header = MemoryProbe.TryReadStruct<InventoryContainer>(address, out _);
        reason = "container unavailable";
        if (!header.HasValue) return false;
        container = header.Value;
        reason = "container not loaded; open the corresponding storage in game";
        if (!container.IsLoaded) return false;
        reason = "invalid container layout";
        if (container.Type != expected || container.Size is < 1 or > 200 || container.Items == null) return false;
        var length = checked(container.Size * sizeof(InventoryItem));
        var data = MemoryProbe.TryReadBytes((ulong)container.Items, length, out _);
        reason = "container slots unreadable";
        if (data is null || data.Length != length) return false;
        bytes = data;
        reason = string.Empty;
        return true;
    }
}
