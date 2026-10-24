using System;
using System.Globalization;
using System.Linq;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;
using NumericsVector3 = System.Numerics.Vector3;

namespace DalamudMCP.Tools;

/// <summary>
/// Auto-walks the character to a destination by delegating pathfinding to the vnavmesh plugin
/// over IPC. Nothing here reimplements navigation: when vnavmesh is absent the call reports
/// that clearly instead of pretending to move.
///
/// The IPC surface itself lives in <see cref="NavmeshBridge"/>, shared with the position and
/// coordinate tools so there is one place that knows how vnavmesh is addressed.
/// </summary>
internal static class MovementTools
{
    private const double DefaultMaxDistance = 30.0;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "move_to_entity",
            "Move to entity",
            "Starts auto-movement toward a game object, following a real navigation mesh. Requires " +
            "the vnavmesh plugin; the destination is snapped to the floor mesh before moving.",
            Json.Schema(
                ("gameObjectId", "string", "Game object id, decimal or 0x-prefixed hex", true),
                ("allowFlight", "boolean", "Permit flying where the zone allows it (default: false)", false)),
            args => MoveToEntity(svc, args),
            mutating: true);

        registry.Add(
            "move_to_nearby_targetable_object",
            "Move to nearby targetable object",
            "Finds the closest targetable object whose name contains the given text and starts " +
            "auto-movement toward it. Requires the vnavmesh plugin.",
            Json.Schema(
                ("name", "string", "Substring of the object name to walk to", true),
                ("maxDistance", "number", "Search radius in yalms (default: 30)", false),
                ("includePlayers", "boolean", "Also consider player characters as destinations (default: false)", false),
                ("allowFlight", "boolean", "Permit flying where the zone allows it (default: false)", false)),
            args => MoveToNearby(svc, args),
            mutating: true);
    }

    // ------------------------------------------------------------------
    // Handlers
    // ------------------------------------------------------------------

    private static JObject MoveToEntity(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var raw = args.Value<string>("gameObjectId")?.Trim();
        if (string.IsNullOrEmpty(raw))
            throw new ToolException("gameObjectId is required");
        if (!TryParseGameObjectId(raw, out var objectId))
            throw new ToolException($"'{raw}' is not a valid game object id");

        var target = svc.ObjectTable.SearchById(objectId)
            ?? throw new ToolException($"no object with id {raw} is currently loaded");

        return StartMove(svc, target, args.Value<bool?>("allowFlight") ?? false);
    }

    private static JObject MoveToNearby(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var name = args.Value<string>("name")?.Trim();
        if (string.IsNullOrEmpty(name))
            throw new ToolException("name is required");

        var localPlayer = svc.ObjectTable.LocalPlayer
            ?? throw new ToolException("the local player is not available yet");

        var maxDistance = args.Value<double?>("maxDistance") ?? DefaultMaxDistance;
        if (maxDistance <= 0)
            throw new ToolException("maxDistance must be greater than zero");

        var includePlayers = args.Value<bool?>("includePlayers") ?? false;

        IGameObject? target = null;
        var best = double.MaxValue;
        foreach (var candidate in svc.ObjectTable)
        {
            if (candidate is null) continue;
            if (candidate.GameObjectId == localPlayer.GameObjectId) continue;
            if (!candidate.IsTargetable) continue;
            if (!includePlayers && candidate.ObjectKind == ObjectKind.Pc) continue;

            var candidateName = Conv.Str(candidate.Name);
            if (string.IsNullOrWhiteSpace(candidateName)) continue;
            if (!candidateName.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

            var distance = Distance(localPlayer.Position, candidate.Position);
            if (distance > maxDistance || distance >= best) continue;

            best = distance;
            target = candidate;
        }

        if (target is null)
            throw new ToolException($"no targetable object within {maxDistance:0.#} yalms has a name containing '{name}'");

        var result = StartMove(svc, target, args.Value<bool?>("allowFlight") ?? false);
        result["distance"] = Math.Round(best, 2);
        return result;
    }

    /// <summary>Snaps the destination to the floor mesh and asks vnavmesh to walk there.</summary>
    private static JObject StartMove(GameServices svc, IGameObject target, bool allowFlight)
    {
        var navmesh = new NavmeshBridge(svc.PluginInterface);
        var destination = navmesh.PointOnFloor(target.Position);

        var outcome = navmesh.StartPathfind(destination, allowFlight);
        var payload = new JObject
        {
            ["success"] = outcome.Started,
            ["gameObjectId"] = $"0x{target.GameObjectId:X}",
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
            ["objectKind"] = target.ObjectKind.ToString(),
            ["destination"] = Conv.Vec3(destination),
        };

        if (outcome.Reason is not null)
        {
            payload["reason"] = outcome.Reason;
            if (outcome.Reason == "vnavmesh_ipc_missing")
            {
                throw new ToolException(
                    "the vnavmesh plugin is not available over IPC, so movement cannot be started. " +
                    "Install and load vnavmesh to use movement tools.");
            }
        }

        return payload;
    }

    private static void EnsureLoggedIn(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");
    }

    private static double Distance(NumericsVector3 a, NumericsVector3 b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        var dz = a.Z - b.Z;
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    /// <summary>
    /// Accepts either the decimal id the object table uses or the 0x-prefixed hex form the tools
    /// also advertise; shared with <see cref="NavTools"/> so both spellings behave identically.
    /// </summary>
    internal static bool TryParseGameObjectId(string value, out ulong objectId)
    {
        var text = value.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out objectId)
            : ulong.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out objectId);
    }
}
