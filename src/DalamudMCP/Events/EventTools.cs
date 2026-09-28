using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Events;

/// <summary>
/// Query and configure the event collector. Reading is always allowed; the collector
/// itself only records state, so these tools stay non-mutating.
/// </summary>
internal static class EventTools
{
    public static void Register(ToolRegistry registry, GameServices svc, EventHub hub)
    {
        registry.Add(
            "query_events",
            "Query events",
            "Returns recently recorded game events (newest first). Event types: hp_change, mp_change, " +
            "gp_change, player_move, job_change, target_change, focus_target_change, target_hp_change, " +
            "combat_damage, combat_start, combat_end, map_change, mount_change, duty_update, fate_update, " +
            "nearby_enemy, nearby_player.",
            Json.Schema(
                ("types", "array of string", "Optional filter: only these event types", false),
                ("count", "integer", "Max events to return (1-500, default 50)", false),
                ("since", "integer", "Only events with timestamp >= this Unix ms time", false),
                ("before", "integer", "Only events with timestamp <= this Unix ms time", false)),
            args => Query(hub, args));

        registry.Add(
            "configure_event_collection",
            "Configure event collection",
            "Changes what the event collector records. Categories: playerStats[hp,mp,gp,job,position], " +
            "targetStats[hp,type,targetChange], objectTypes[enemy], combatEvents[damage,startEnd], " +
            "systemEvents[duty,fate]. objectRange/nearbyPlayerRange 0-200 (0 = off), throttleMs 50-60000.",
            Json.Schema(
                ("playerStats", "array of string", "Player state to watch", false),
                ("targetStats", "array of string", "Target state to watch", false),
                ("objectRange", "integer", "Enemy scan radius in yalms (0 disables)", false),
                ("objectTypes", "array of string", "Object categories to watch", false),
                ("nearbyPlayerRange", "integer", "Player scan radius in yalms (0 disables)", false),
                ("combatEvents", "array of string", "Combat events to record", false),
                ("systemEvents", "array of string", "System events to record", false),
                ("throttleMs", "integer", "Minimum ms between two records of one event type", false)),
            args => Configure(svc, hub, args));

        registry.Add(
            "get_event_config",
            "Get event config",
            "Current event collection configuration and buffer usage.",
            Json.Schema(),
            _ => GetConfig(hub));

        // Waiting cannot run on the framework thread: the game loop would stall for the whole
        // timeout. It is registered as an async tool, so the wait happens on the request's own
        // thread while the client keeps rendering.
        registry.AddAsync(
            "events_wait",
            "Wait for events",
            "Blocks until a matching event is recorded, or the timeout elapses. Pass afterId from " +
            "a previous query (its lastId) to wait for something newer. Returns the new events " +
            "oldest first, plus timedOut=true when nothing arrived in time.",
            Json.Schema(
                ("afterId", "integer", "Only events newer than this cursor (default: the newest so far)", false),
                ("types", "array of string", "Optional filter: only these event types", false),
                ("count", "integer", "Max events to return (1-500, default 100)", false),
                ("timeoutMs", "integer", "How long to wait in milliseconds (1-30000, default 10000)", false)),
            args => Wait(hub, args));
    }

    private static async Task<object?> Wait(EventHub hub, JObject args)
    {
        var collector = hub.Collector;
        if (collector is null)
            return new JObject { ["error"] = "event collector is not running" };

        var afterId = args.TryGetValue("afterId", out var afterTok) && afterTok.Type != JTokenType.Null
            ? afterTok.Value<long>()
            : hub.Buffer.LastId;

        var types = (args["types"] as JArray)?
            .Select(t => t.Value<string>() ?? string.Empty)
            .Where(t => t.Length > 0).ToArray();

        var count = Math.Clamp(args.Value<int?>("count") ?? 100, 1, 500);
        var timeoutMs = Math.Clamp(args.Value<int?>("timeoutMs") ?? 10000, 1, 30000);

        var result = await hub.Buffer
            .WaitAsync(afterId, types, count, TimeSpan.FromMilliseconds(timeoutMs), CancellationToken.None)
            .ConfigureAwait(false);

        return new JObject
        {
            ["count"] = result.Events.Count,
            ["timedOut"] = result.TimedOut,
            ["afterId"] = afterId,
            ["lastId"] = hub.Buffer.LastId,
            ["events"] = JArray.FromObject(result.Events),
            ["config"] = ConfigJson(collector.Config),
        };
    }

    private static JObject Query(EventHub hub, JObject args)
    {
        var collector = hub.Collector;
        if (collector is null)
            return new JObject { ["error"] = "event collector is not running" };

        var count = args.Value<int?>("count") ?? 50;
        count = Math.Clamp(count, 1, 500);
        var types = args["types"] as JArray;
        var typeList = types?.Select(t => t.Value<string>() ?? string.Empty)
            .Where(t => t.Length > 0).ToArray();

        long? since = args.TryGetValue("since", out var sinceTok) ? sinceTok.Value<long>() : null;
        long? before = args.TryGetValue("before", out var beforeTok) ? beforeTok.Value<long>() : null;

        var events = hub.Buffer.Query(typeList, count, since, before);
        return new JObject
        {
            ["count"] = events.Count,
            ["events"] = JArray.FromObject(events),
            ["config"] = ConfigJson(collector.Config),
        };
    }

    private static JObject Configure(GameServices svc, EventHub hub, JObject args)
    {
        var collector = hub.Collector;
        if (collector is null)
            return new JObject { ["error"] = "event collector is not running" };

        // Start from the live config so a partial patch keeps unspecified settings.
        var config = JObject.FromObject(collector.Config);
        foreach (var prop in args.Properties())
            config[prop.Name] = prop.Value;

        EventCollectionConfig parsed;
        try
        {
            parsed = config.ToObject<EventCollectionConfig>(PatchSerializer())
                ?? throw new ArgumentException("config did not deserialize to an object");
            parsed.Validate();
        }
        catch (Exception ex)
        {
            throw new ToolException($"invalid config: {ex.Message}");
        }

        collector.UpdateConfig(parsed);
        svc.Config.EventCollection = parsed;
        svc.PluginInterface!.SavePluginConfig(svc.Config);

        return new JObject
        {
            ["success"] = true,
            ["config"] = ConfigJson(parsed),
        };
    }

    private static JObject GetConfig(EventHub hub)
    {
        var collector = hub.Collector;
        if (collector is null)
            return new JObject { ["error"] = "event collector is not running" };

        return new JObject
        {
            ["config"] = ConfigJson(collector.Config),
            ["bufferSize"] = hub.Buffer.Count,
            ["maxBufferSize"] = hub.Buffer.MaxSize,
        };
    }

    private static JObject ConfigJson(EventCollectionConfig config) => JObject.FromObject(config);

    /// <summary>
    /// Deserializer for a partial patch. Newtonsoft's default
    /// <see cref="ObjectCreationHandling.Auto"/> reuses the list instances created by the
    /// property initializers and <em>adds</em> the incoming array items on top of them, so
    /// patching one unrelated field would re-append every default
    /// (<c>["hp","mp","gp","job","position"]</c> becoming 10, then 15, then 20 entries).
    /// <see cref="ObjectCreationHandling.Replace"/> makes each incoming array overwrite its
    /// property instead of extending it.
    /// </summary>
    private static JsonSerializer PatchSerializer() => new()
    {
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };
}
