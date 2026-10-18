using System;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace DalamudMCP.Events;

internal static class CombatPacketReader
{
    public static (ActionEffectHandler.Header Header, byte[] Effects, byte[] Targets)? Copy(ulong header, ulong effects, ulong targets)
    {
        var copied = MemoryProbe.TryReadStruct<ActionEffectHandler.Header>(header, out _);
        if (copied is not { } h || h.NumTargets > 32) return null;
        var n = h.NumTargets;
        var effectBytes = n == 0 ? Array.Empty<byte>() : MemoryProbe.TryReadBytes(effects, n * 64, out _);
        var targetBytes = n == 0 ? Array.Empty<byte>() : MemoryProbe.TryReadBytes(targets, n * 8, out _);
        return effectBytes?.Length == n * 64 && targetBytes?.Length == n * 8 ? (h, effectBytes, targetBytes) : null;
    }
}
