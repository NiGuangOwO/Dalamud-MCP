using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ActionRow = Lumina.Excel.Sheets.Action;

namespace DalamudMCP.Events;

/// <summary>Opt-in server-effect capture. Native data is copied immediately; game APIs run only on Update.</summary>
internal sealed unsafe class CombatRecorder : IDisposable
{
    private readonly GameServices svc;
    private Hook<ActionEffectHandler.Delegates.Receive>? hook;
    private readonly ConcurrentQueue<(long Time, uint Source, ActionEffectHandler.Header Header, byte[] Effects, byte[] Targets)> pending = new();
    private CombatRecording recording = new();
    private readonly Dictionary<uint, (uint Owner, ulong Id)> actors = new();
    private readonly Dictionary<(uint, uint), (ushort Param, uint Source)> statuses = new();
    private readonly Dictionary<uint, uint> casts = new();
    private readonly Dictionary<uint, bool> deaths = new();
    private readonly Dictionary<uint, string> actionNames = new();
    private volatile bool enabled;
    private int pendingCount;
    private int droppedPackets;
    private int invalidPackets;
    private uint localId;
    private uint territory;
    private bool allActors;
    private long deadline;
    private long lastSample;
    private bool firstFrame;
    private const string Coverage = "Server action effects (raw effects, direct damage/heal), sampled casts/statuses/deaths at 100ms. Not an FFLogs parser: no periodic DoT/HoT tick attribution, cast requests, guaranteed cast-end reason, full encounter mechanics, or upload. Effect amounts are server announcements, not confirmed effective HP loss/healing. Actor names are omitted. In-memory until exported; reload loses records.";

    public CombatRecorder(ToolRegistry registry, GameServices services)
    {
        svc = services;
        registry.Add("combat_recording_configure", "Configure combat recording",
            "Opt in to bounded recording. Entering combat opens an encounter; leaving combat closes it. Enabling starts a NEW recording buffer; export existing encounters first. Disabling retains encounters. Automatically disables on timeout or listener stop. " + Coverage,
            Json.Schema(("enabled", "boolean", "Enable or disable capture", true),
                ("scope", "string", "local (default, own/pet actions plus effects targeting self), or all (nearby visible actors)", false),
                ("maxEvents", "integer", "Events per encounter, 100-50000 (default 20000)", false),
                ("maxEncounters", "integer", "Retained encounters, 1-10 (default 5)", false),
                ("durationSeconds", "integer", "Capture lifetime, 10-1800 seconds (default 600)", false)), Configure, mutating: true);
        registry.Add("combat_recording_status", "Combat recording status", Coverage, Json.Schema(), _ => Status());
        registry.Add("combat_recording_list", "List combat encounters", "Lists retained encounters oldest first, including truncation/partial-start metadata.",
            Json.Schema(), _ => new { encounters = recording.Encounters.Select(e => e.Summary()).ToArray(), recording.EvictedEncounters });
        registry.Add("combat_recording_read", "Read combat timeline", "Chronological capture order with millisecond timestamps and elapsedMs (pre-pull can be negative). Cursor is event id; no events are drained.",
            Json.Schema(("encounterId", "string", "Encounter id; latest if omitted", false),
                ("afterId", "integer", "Exclusive event cursor, default 0", false),
                ("count", "integer", "Page size, 1-500, default 100", false)),
            a => recording.Read(a.Value<string>("encounterId"), a.Value<long?>("afterId") ?? 0, a.Value<int?>("count") ?? 100));
        registry.Add("combat_recording_export", "Export combat encounter", "Writes one retained encounter as JSON under the plugin config combat-recordings folder. No arbitrary path. Keeps the most recent 10 exports. Includes coverage limitations and raw effects.",
            Json.Schema(("encounterId", "string", "Encounter id; latest if omitted", false)), Export);
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private static int Bounded(JObject args, string key, int fallback, int min, int max)
    {
        var value = args.Value<int?>(key) ?? fallback;
        if (value < min || value > max) throw new ToolException($"{key} must be {min}-{max}");
        return value;
    }

    private object Configure(JObject args)
    {
        if (args["enabled"]?.Type != JTokenType.Boolean) throw new ToolException("enabled must be a boolean");
        if (!args.Value<bool>("enabled")) { Stop("disabled"); return Status(); }
        var scope = args.Value<string>("scope") ?? "local";
        if (scope != "local" && scope != "all") throw new ToolException("scope must be local or all");
        var maxEvents = Bounded(args, "maxEvents", 20000, 100, 50000);
        var maxEncounters = Bounded(args, "maxEncounters", 5, 1, 10);
        var duration = Bounded(args, "durationSeconds", 600, 10, 1800);
        if (!svc.ClientState.IsLoggedIn || svc.ObjectTable.LocalPlayer == null) throw new ToolException("Log in before enabling combat capture");
        Stop("reconfigured");
        var address = (nint)ActionEffectHandler.MemberFunctionPointers.Receive;
        if (MemoryProbe.Query((ulong)address) is not { Executable: true, Readable: true, Guarded: false, Committed: true })
            throw new ToolException("SDK ActionEffectHandler.Receive is unresolved or non-executable; no hook installed");
        try
        {
            hook = svc.Interop.HookFromAddress<ActionEffectHandler.Delegates.Receive>(address, Receive);
            recording = new CombatRecording(maxEvents, maxEncounters);
            allActors = scope == "all";
            droppedPackets = invalidPackets = 0;
            territory = svc.ClientState.TerritoryType;
            deadline = Now + duration * 1000L;
            firstFrame = true;
            enabled = true;
            svc.Framework.Update += Update;
            Update(svc.Framework);
            hook.Enable();
        }
        catch (Exception ex)
        {
            Stop("enable_failed");
            throw new ToolException($"Combat capture could not start: {ex.Message}");
        }
        return Status();
    }

    private object Status() => new { enabled, hooked = hook?.IsEnabled == true, scope = allActors ? "all" : "local",
        buildMvid = typeof(CombatRecorder).Assembly.ManifestModule.ModuleVersionId,
        expiresAt = enabled ? deadline : (long?)null, activeEncounterId = recording.Active?.Id,
        retainedEncounters = recording.Encounters.Count, recording.MaxEvents, recording.MaxEncounters,
        recording.EvictedEncounters, droppedPackets, invalidPackets, pendingPackets = pendingCount, coverage = Coverage };

    private void Receive(uint source, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        // Snapshot arguments before Original; it may consume/modify packet storage. Never dereference caller pointers.
        try
        {
            if (enabled)
            {
                var copied = CombatPacketReader.Copy((ulong)header, (ulong)effects, (ulong)targets);
                if (copied is { } packet)
                {
                    if (System.Threading.Interlocked.Increment(ref pendingCount) <= 2048)
                        pending.Enqueue((Now, source, packet.Header, packet.Effects, packet.Targets));
                    else { System.Threading.Interlocked.Decrement(ref pendingCount); System.Threading.Interlocked.Increment(ref droppedPackets); }
                }
                else System.Threading.Interlocked.Increment(ref invalidPackets);
            }
        }
        catch (Exception) { System.Threading.Interlocked.Increment(ref invalidPackets); }
        hook!.Original(source, caster, position, header, effects, targets);
    }

    private void Update(IFramework framework)
    {
        if (!enabled) return;
        try
        {
            var now = Now;
            var player = svc.ObjectTable.LocalPlayer;
            if (!svc.Config.AllowMutatingTools) { Stop("mutation_gate_closed"); return; }
            if (now >= deadline) { Drain(); Stop("timeout"); return; }
            if (player == null || !svc.ClientState.IsLoggedIn) { Drain(); Stop("logout"); return; }
            if (territory != svc.ClientState.TerritoryType) { Drain(); recording.End(now, "territory_changed"); territory = svc.ClientState.TerritoryType; statuses.Clear(); casts.Clear(); deaths.Clear(); }
            localId = player.EntityId;
            actors.Clear();
            foreach (var actor in svc.ObjectTable) actors[actor.EntityId] = (actor.OwnerId, actor.GameObjectId);
            var inCombat = svc.Condition[ConditionFlag.InCombat];
            if (inCombat && recording.Active == null)
            {
                recording.Begin(now, territory, player.ClassJob.RowId, firstFrame);
                statuses.Clear(); casts.Clear(); deaths.Clear();
            }
            Drain();
            if (!inCombat && recording.Active != null) recording.End(now, "combat_exit");
            firstFrame = false;
            if (recording.Active == null || now - lastSample < 100) return;
            lastSample = now;
            var current = new HashSet<(uint, uint)>();
            foreach (var actor in svc.ObjectTable.OfType<Dalamud.Game.ClientState.Objects.Types.IBattleChara>().Take(256))
            {
                if (!allActors && actor.EntityId != localId && actor.OwnerId != localId) continue;
                var source = actor.EntityId;
                if (!deaths.TryGetValue(source, out var dead) || dead != actor.IsDead)
                    recording.Record(now, new JObject { ["type"] = "actor_snapshot", ["sourceId"] = source,
                        ["ownerId"] = actor.OwnerId, ["hp"] = actor.CurrentHp, ["dead"] = actor.IsDead, ["jobId"] = actor.ClassJob.RowId });
                deaths[source] = actor.IsDead;
                var cast = actor.IsCasting ? actor.CastActionId : 0;
                casts.TryGetValue(source, out var oldCast);
                if (cast != oldCast)
                    recording.Record(now, new JObject { ["type"] = cast == 0 ? "cast_end_observed" : "cast_start_observed",
                        ["sourceId"] = source, ["actionId"] = cast == 0 ? oldCast : cast,
                        ["targetId"] = actor.CastTargetObjectId, ["totalCastTime"] = actor.TotalCastTime });
                casts[source] = cast;
                foreach (var status in actor.StatusList)
                {
                    if (status.StatusId == 0) continue;
                    var key = (source, status.StatusId);
                    current.Add(key);
                    var value = (status.Param, status.SourceId);
                    if (!statuses.TryGetValue(key, out var previous) || previous != value)
                        recording.Record(now, new JObject { ["type"] = "status_observed", ["targetId"] = source,
                            ["statusId"] = status.StatusId, ["sourceId"] = status.SourceId,
                            ["param"] = status.Param, ["remainingTime"] = status.RemainingTime });
                    statuses[key] = value;
                }
            }
            foreach (var key in statuses.Keys.Where(k => !current.Contains(k)).ToArray())
            {
                recording.Record(now, new JObject { ["type"] = "status_removed_observed", ["targetId"] = key.Item1, ["statusId"] = key.Item2 });
                statuses.Remove(key);
            }
            foreach (var id in casts.Keys.Where(id => !actors.ContainsKey(id)).ToArray()) { casts.Remove(id); deaths.Remove(id); }
        }
        catch (Exception ex) { svc.LogError($"Combat recorder stopped: {ex.Message}"); Stop("collector_error"); }
    }

    private void Drain()
    {
        for (var i = 0; i < 2048 && pending.TryDequeue(out var p); i++)
        {
            System.Threading.Interlocked.Decrement(ref pendingCount);
            var targetIds = MemoryMarshal.Cast<byte, ulong>(p.Targets.AsSpan());
            actors.TryGetValue(p.Source, out var actor);
            var own = p.Source == localId || actor.Owner == localId;
            if (!allActors && !own && !targetIds.Contains((ulong)localId)) continue;
            var targets = new JArray();
            for (var t = 0; t < targetIds.Length; t++)
            {
                var entries = new JArray();
                for (var e = 0; e < 8; e++)
                {
                    var offset = t * 64 + e * 8;
                    var effect = MemoryMarshal.Read<ActionEffectHandler.Effect>(p.Effects.AsSpan(offset, 8));
                    if (effect.Type == 0) continue;
                    entries.Add(CombatEffectJson.Decode(effect.Type, effect.Param0, effect.Param1, effect.Param2, effect.Param3, effect.Param4, effect.Value));
                }
                targets.Add(new JObject { ["targetId"] = targetIds[t], ["effects"] = entries });
            }
            string? name = null;
            if ((byte)p.Header.ActionType == 1)
            {
                if (!actionNames.TryGetValue(p.Header.ActionId, out name))
                {
                    name = svc.Sheet<ActionRow>().GetRowOrDefault(p.Header.ActionId)?.Name.ExtractText() ?? "";
                    if (actionNames.Count < 4096) actionNames[p.Header.ActionId] = name;
                }
            }
            recording.Record(p.Time, new JObject { ["type"] = "action_effect", ["sourceId"] = p.Source,
                ["ownerId"] = actor.Owner, ["actionId"] = p.Header.ActionId, ["actionName"] = name,
                ["actionType"] = (byte)p.Header.ActionType, ["globalSequence"] = p.Header.GlobalSequence,
                ["sourceSequence"] = p.Header.SourceSequence, ["animationLock"] = p.Header.AnimationLock,
                ["targets"] = targets }, allowPreRoll: own);
        }
    }

    private object Export(JObject args)
    {
        var fight = recording.Get(args.Value<string>("encounterId"));
        var dir = Path.Combine(svc.PluginInterface!.GetPluginConfigDirectory(), "combat-recordings");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"combat-{fight.Id}.json");
        var json = JsonConvert.SerializeObject(new { formatVersion = 1, coverage = Coverage,
            buildMvid = typeof(CombatRecorder).Assembly.ManifestModule.ModuleVersionId,
            pluginVersion = typeof(CombatRecorder).Assembly.GetName().Version?.ToString(), scope = allActors ? "all" : "local",
            packetLoss = new { droppedPackets, invalidPackets }, encounter = fight.Summary(), events = fight.Events }, Formatting.Indented);
        File.WriteAllText(path + ".tmp", json);
        File.Move(path + ".tmp", path, true);
        foreach (var old in new DirectoryInfo(dir).GetFiles("combat-*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(10)) old.Delete();
        return new { path, eventCount = fight.Events.Count, bytes = new FileInfo(path).Length };
    }

    public void Stop(string reason = "listener_stopped")
    {
        enabled = false;
        svc.Framework.Update -= Update;
        hook?.Disable();
        hook?.Dispose();
        hook = null;
        recording.End(Now, reason);
        while (pending.TryDequeue(out _)) System.Threading.Interlocked.Decrement(ref pendingCount);
        actors.Clear(); statuses.Clear(); casts.Clear(); deaths.Clear(); actionNames.Clear();
    }
    public void Dispose() => Stop("plugin_unloaded");
}
