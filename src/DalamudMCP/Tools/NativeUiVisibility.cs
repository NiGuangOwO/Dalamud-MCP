using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

/// <summary>Guarded effective visibility. Caches belong to one framework-thread observation only.</summary>
internal static unsafe class NativeUiVisibility
{
    internal static bool? Combine(bool? left, bool? right) =>
        left == false || right == false ? false : left == null || right == null ? null : true;

    internal static bool? Read(nint address, Dictionary<nint, bool?>? cache = null)
    {
        if (address == 0) return null;
        var path = new List<nint>();
        var seen = new HashSet<nint>();
        var current = address;
        bool? result = true;
        while (current != 0)
        {
            if (cache != null && cache.TryGetValue(current, out result)) break;
            if (path.Count >= 64 || !seen.Add(current)) { result = null; break; }
            path.Add(current);
            var node = MemoryProbe.TryReadStruct<AtkResNode>((ulong)current, out _);
            if (!node.HasValue) { result = null; break; }
            if ((node.Value.NodeFlags & NodeFlags.Visible) == 0) { result = false; break; }
            current = (nint)node.Value.ParentNode;
            result = true;
        }
        if (cache != null)
            foreach (var item in path)
                if (cache.Count < 8192) cache[item] = result;
        return result;
    }
}
