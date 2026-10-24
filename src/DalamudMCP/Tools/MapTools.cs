using System;
using System.Text;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Bounded copies of the map agent; never invokes a map handler.</summary>
internal static unsafe class MapTools
{
    public static void Register(ToolRegistry registry)
    {
        var schema = Json.Schema(
            ("markerOffset", "integer", "First native static-marker array index (default 0, 0-132). Re-read selectedMapId between pages.", false),
            ("maxMarkers", "integer", "Maximum static markers returned (default 40, 1-132).", false));
        schema["properties"]!["markerOffset"]!["minimum"] = 0;
        schema["properties"]!["markerOffset"]!["maximum"] = 132;
        schema["properties"]!["maxMarkers"]!["minimum"] = 1;
        schema["properties"]!["maxMarkers"]!["maximum"] = 132;
        registry.Add("get_map_state", "Read current and selected map state",
            "Reads the map agent's current/selected map and territory ids, size factors, offsets, title, and a bounded page " +
            "of static map markers (icon ids, data keys, native coordinates and optional subtext). Coordinates are raw " +
            "MapMarkerBase.X/Y, NOT world coordinates or player map coordinates; do not pass them to movement tools. " +
            "This is not a complete inventory of quests, dynamic objects, flags or temporary markers. Hidden-map selection " +
            "is cached state; inspect mapVisible/mapReady. No map opening, marker clicking, travel or input.", schema, args =>
            {
                var offset = Integer(args, "markerOffset", 0, 0, 132);
                var max = Integer(args, "maxMarkers", 40, 1, 132);
                var window = UiTools.ResolveAddon("AreaMap", null);
                var windowState = MemoryProbe.TryReadStruct<AtkUnitBase>((ulong)window, out _);
                JObject result;
                try { result = ReadAt((nint)AgentMap.Instance(), offset, max); }
                catch (InvalidOperationException) { result = new JObject { ["available"] = false, ["reason"] = "map agent unresolved" }; }
                result["mapVisible"] = windowState.HasValue ? windowState.Value.IsVisible : (bool?)null;
                result["mapReady"] = windowState.HasValue ? windowState.Value.IsReady : (bool?)null;
                return result;
            });
    }

    internal static int Integer(JObject args, string name, int fallback, int min, int max)
    {
        if (args[name] == null) return fallback;
        if (args[name]!.Type != JTokenType.Integer || !int.TryParse(args[name]!.ToString(), out var value) || value < min || value > max)
            throw new ToolException($"{name} must be an integer from {min} to {max}");
        return value;
    }

    internal static string? LabelAt(nint pointer)
    {
        if (pointer == 0) return null;
        var bytes = MemoryProbe.TryReadBytes((ulong)pointer, 512, out _);
        if (bytes == null) return null;
        var end = Array.IndexOf(bytes, (byte)0);
        // Do not silently turn a truncated buffer into a complete label.
        return end < 0 ? null : Encoding.UTF8.GetString(bytes, 0, end);
    }

    internal static JObject ReadAt(nint pointer, int offset, int max)
    {
        if (offset < 0 || offset > 132 || max < 1 || max > 132) throw new ToolException("invalid map marker page");
        var copied = MemoryProbe.TryReadStruct<AgentMap>((ulong)pointer, out _);
        if (!copied.HasValue) return new JObject { ["available"] = false, ["reason"] = "map agent unreadable" };
        var map = copied.Value;
        var markers = map.MapMarkers;
        var count = (int)map.MapMarkerCount;
        var invalid = count > markers.Length;
        var entries = new JArray();
        var end = invalid ? offset : Math.Min(count, offset + max);
        for (var i = offset; i < end; i++)
        {
            var marker = markers[i];
            var data = marker.MapMarker;
            entries.Add(new JObject {
                ["arrayIndex"] = i, ["iconId"] = data.IconId, ["secondaryIconId"] = data.SecondaryIconId,
                ["dataType"] = marker.DataType, ["dataKey"] = marker.DataKey, ["subKey"] = marker.MapMarkerSubKey,
                ["nativeX"] = data.X, ["nativeY"] = data.Y,
                ["subtext"] = LabelAt((nint)(byte*)data.Subtext) });
        }
        return new JObject {
            ["available"] = true,
            ["currentTerritoryId"] = map.CurrentTerritoryId, ["currentMapId"] = map.CurrentMapId,
            ["selectedTerritoryId"] = map.SelectedTerritoryId, ["selectedMapId"] = map.SelectedMapId,
            ["selectedMapSub"] = map.SelectedMapSub,
            ["currentSizeFactor"] = map.CurrentMapSizeFactor, ["selectedSizeFactor"] = map.SelectedMapSizeFactor,
            ["currentOffsetX"] = map.CurrentOffsetX, ["currentOffsetY"] = map.CurrentOffsetY,
            ["selectedOffsetX"] = map.SelectedOffsetX, ["selectedOffsetY"] = map.SelectedOffsetY,
            ["title"] = LabelAt((nint)(byte*)map.MapTitleString.StringPtr),
            ["coordinateSpace"] = "native MapMarkerBase.X/Y (not world coordinates)",
            ["markerOffset"] = offset, ["totalStaticMarkers"] = count, ["count"] = entries.Count,
            ["nextMarkerOffset"] = !invalid && end < count ? end : null,
            ["truncated"] = !invalid && end < count, ["scanIncomplete"] = invalid,
            ["reason"] = invalid ? "static marker count exceeds installed SDK capacity" : null,
            ["markers"] = entries };
    }
}
