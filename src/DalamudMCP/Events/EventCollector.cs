using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using DalamudMCP.Tools;

namespace DalamudMCP.Events;

/// <summary>
/// Watches player, target and surroundings state every framework tick and records
/// changes into an <see cref="EventBuffer"/>. Purely additive: it never mutates game
/// state, so it can run with the mutating-tool gate off.
/// </summary>
public sealed class EventCollector : IDisposable
{
    private readonly GameServices svc;
    private readonly EventBuffer buffer;
    private readonly object configLock = new();
    private EventCollectionConfig config;
    private bool disposed;

    // Baseline snapshot; populated on the first tick after login so the collector does
    // not emit a burst of synthetic "change" events at startup.
    private bool initialized;
    private uint lastHp;
    private uint lastMp;
    private uint lastJob;
    private uint lastGp;
    private System.Numerics.Vector3 lastPosition;
    private ulong? lastTargetId;
    private ulong? lastFocusTargetId;
    private long lastTargetHp;
    private ulong lastTargetHpObjectId;
    private bool lastInCombat;
    private bool lastMounted;
    private bool lastDutyStarted;
    private uint lastTerritory;
    private HashSet<ulong> lastEnemyIds = new();
    private HashSet<ulong> lastPlayerIds = new();
    private Dictionary<uint, ushort> lastFates = new();
    private readonly Dictionary<string, long> lastEventTime = new();

    public EventCollector(GameServices svc, EventBuffer buffer, EventCollectionConfig config)
    {
        this.svc = svc;
        this.buffer = buffer;
        this.config = config;
        svc.Framework.Update += OnUpdate;
    }

    public EventCollectionConfig Config
    {
        get
        {
            lock (configLock) return config;
        }
    }

    public void UpdateConfig(EventCollectionConfig value)
    {
        lock (configLock)
        {
            config = value;
            // New settings change what is comparable, so the next tick re-baselines.
            initialized = false;
        }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        svc.Framework.Update -= OnUpdate;
    }

    private void OnUpdate(IFramework framework)
    {
        if (disposed) return;

        try
        {
            if (!svc.ClientState.IsLoggedIn)
            {
                initialized = false;
                return;
            }

            var player = svc.ObjectTable.LocalPlayer;
            if (player is null)
            {
                initialized = false;
                return;
            }

            if (!initialized)
            {
                InitializeSnapshot(player);
                initialized = true;
                return;
            }
            CollectPlayerStats(player);
            CollectTargetStats();
            CollectCombatEvents();
            CollectMapEvents();
            CollectObjects(player);
            CollectFates();
        }
        catch
        {
            // A transient state mid-zone must never take down the framework tick.
        }
    }

    private void InitializeSnapshot(ICharacter player)
    {
        lock (configLock)
        {
            lastHp = player.CurrentHp;
            lastMp = player.CurrentMp;
            lastJob = player.ClassJob.RowId;
            lastPosition = player.Position;
            lastGp = player.CurrentGp;

            lastTargetId = svc.TargetManager.Target?.GameObjectId;
            lastFocusTargetId = svc.TargetManager.FocusTarget?.GameObjectId;
            var target = svc.TargetManager.Target;
            lastTargetHp = target is ICharacter c ? c.CurrentHp : 0;
            lastTargetHpObjectId = target?.GameObjectId ?? 0;

            lastInCombat = svc.Condition[ConditionFlag.InCombat];
            lastMounted = svc.Condition[ConditionFlag.Mounted];
            lastDutyStarted = SafeDutyStarted();
            lastTerritory = svc.ClientState.TerritoryType;

            lastEnemyIds = NearbyIds(player, config.ObjectRange, playersOnly: false);
            lastPlayerIds = NearbyIds(player, config.NearbyPlayerRange, playersOnly: true);

            lastFates.Clear();
            foreach (var fate in svc.FateTable)
                if (svc.FateTable.IsValid(fate)) lastFates[fate.FateId] = fate.Progress;
        }

        lastEventTime.Clear();
    }

    private void CollectPlayerStats(ICharacter player)
    {
        var cfg = Config;
        var now = NowMs();

        if (cfg.HasPlayerStat("hp") && lastHp != player.CurrentHp)
        {
            Add(EventTypes.HpChange, now, new { from = lastHp, to = player.CurrentHp, max = player.MaxHp });
            if (cfg.HasCombatEvent("damage") && player.CurrentHp < lastHp)
            {
                Add(EventTypes.CombatDamage, now, new
                {
                    source = "player",
                    amount = lastHp - player.CurrentHp,
                    currentHp = player.CurrentHp,
                    maxHp = player.MaxHp,
                });
            }

            lastHp = player.CurrentHp;
        }

        if (cfg.HasPlayerStat("mp") && lastMp != player.CurrentMp)
        {
            Add(EventTypes.MpChange, now, new { from = lastMp, to = player.CurrentMp, max = player.MaxMp });
            lastMp = player.CurrentMp;
        }

        if (cfg.HasPlayerStat("gp") && lastGp != player.CurrentGp)
        {
            Add(EventTypes.GpChange, now, new { from = lastGp, to = player.CurrentGp });
            lastGp = player.CurrentGp;
        }

        if (cfg.HasPlayerStat("job") && lastJob != player.ClassJob.RowId)
        {
            var jobName = player.ClassJob.IsValid ? player.ClassJob.Value.Name.ExtractText() : null;
            Add(EventTypes.JobChange, now, new { from = lastJob, to = player.ClassJob.RowId, name = jobName });
            lastJob = player.ClassJob.RowId;
        }

        if (cfg.HasPlayerStat("position"))
        {
            var distance = System.Numerics.Vector3.Distance(lastPosition, player.Position);
            if (distance > 0.5f)
            {
                Add(EventTypes.PlayerMove, now, new
                {
                    x = Conv.Round(player.Position.X),
                    y = Conv.Round(player.Position.Y),
                    z = Conv.Round(player.Position.Z),
                    distance = Conv.Round(distance, 1),
                });
                lastPosition = player.Position;
            }
        }
    }

    private void CollectTargetStats()
    {
        var cfg = Config;
        var now = NowMs();

        var target = svc.TargetManager.Target;
        var targetId = target?.GameObjectId;

        if (cfg.HasTargetStat("targetChange") && targetId != lastTargetId)
        {
            Add(EventTypes.TargetChange, now, new
            {
                from = lastTargetId,
                to = targetId,
                name = target is null ? null : Conv.Str(target.Name),
                kind = target?.ObjectKind.ToString(),
            });
            lastTargetId = targetId;
            lastTargetHp = target is ICharacter tc ? tc.CurrentHp : 0;
            lastTargetHpObjectId = targetId ?? 0;
        }

        var focus = svc.TargetManager.FocusTarget;
        var focusId = focus?.GameObjectId;
        if (focusId != lastFocusTargetId)
        {
            Add(EventTypes.FocusTargetChange, now, new { from = lastFocusTargetId, to = focusId });
            lastFocusTargetId = focusId;
        }

        if (cfg.HasTargetStat("hp") && target is ICharacter ch && targetId == lastTargetHpObjectId)
        {
            if (ch.CurrentHp != lastTargetHp && ThrottleOk(EventTypes.TargetHpChange, now))
            {
                Add(EventTypes.TargetHpChange, now, new
                {
                    targetId = targetId,
                    from = lastTargetHp,
                    to = ch.CurrentHp,
                    max = ch.MaxHp,
                });
                lastTargetHp = ch.CurrentHp;
            }
            else if (ch.CurrentHp != lastTargetHp)
            {
                lastTargetHp = ch.CurrentHp;
            }
        }
    }

    private void CollectCombatEvents()
    {
        var cfg = Config;
        var now = NowMs();

        var inCombat = svc.Condition[ConditionFlag.InCombat];
        if (inCombat != lastInCombat)
        {
            var type = inCombat ? EventTypes.CombatStart : EventTypes.CombatEnd;
            if (cfg.HasCombatEvent("startEnd") && ThrottleOk(type, now))
                Add(type, now, new { inCombat });

            lastInCombat = inCombat;
        }

        var mounted = svc.Condition[ConditionFlag.Mounted];
        if (mounted != lastMounted)
        {
            Add(EventTypes.MountChange, now, new { mounted });
            lastMounted = mounted;
        }
    }

    private void CollectMapEvents()
    {
        var cfg = Config;
        var now = NowMs();

        var territory = svc.ClientState.TerritoryType;
        if (territory != lastTerritory)
        {
            Add(EventTypes.MapChange, now, new { from = lastTerritory, to = territory });
            lastTerritory = territory;
            // Nearby sets are meaningless after a zone change; re-baseline silently.
            if (svc.ObjectTable.LocalPlayer is { } lp)
            {
                lastEnemyIds = NearbyIds(lp, cfg.ObjectRange, playersOnly: false);
                lastPlayerIds = NearbyIds(lp, cfg.NearbyPlayerRange, playersOnly: true);
            }
        }

        if (cfg.HasSystemEvent("duty"))
        {
            var dutyStarted = SafeDutyStarted();
            if (dutyStarted != lastDutyStarted && ThrottleOk(EventTypes.DutyUpdate, now))
            {
                Add(EventTypes.DutyUpdate, now, new { isDutyStarted = dutyStarted });
                lastDutyStarted = dutyStarted;
            }
            else
            {
                lastDutyStarted = dutyStarted;
            }
        }
        else
        {
            lastDutyStarted = SafeDutyStarted();
        }
    }

    private void CollectObjects(ICharacter player)
    {
        var cfg = Config;
        var now = NowMs();

        if (cfg.HasObjectType("enemy") && cfg.ObjectRange > 0)
        {
            var ids = NearbyIds(player, cfg.ObjectRange, playersOnly: false);
            if (!ids.SetEquals(lastEnemyIds) && ThrottleOk(EventTypes.NearbyEnemy, now))
            {
                var enemies = NearbyCharacters(player, cfg.ObjectRange, playersOnly: false);
                Add(EventTypes.NearbyEnemy, now, new
                {
                    range = cfg.ObjectRange,
                    count = enemies.Count,
                    added = ids.Except(lastEnemyIds).ToArray(),
                    removed = lastEnemyIds.Except(ids).ToArray(),
                    enemies,
                });
                lastEnemyIds = ids;
            }
            else
            {
                lastEnemyIds = ids;
            }
        }

        if (cfg.NearbyPlayerRange > 0)
        {
            var ids = NearbyIds(player, cfg.NearbyPlayerRange, playersOnly: true);
            if (!ids.SetEquals(lastPlayerIds) && ThrottleOk(EventTypes.NearbyPlayer, now))
            {
                var players = NearbyCharacters(player, cfg.NearbyPlayerRange, playersOnly: true);
                Add(EventTypes.NearbyPlayer, now, new
                {
                    range = cfg.NearbyPlayerRange,
                    count = players.Count,
                    added = ids.Except(lastPlayerIds).ToArray(),
                    removed = lastPlayerIds.Except(ids).ToArray(),
                    players,
                });
                lastPlayerIds = ids;
            }
            else
            {
                lastPlayerIds = ids;
            }
        }
    }

    private void CollectFates()
    {
        var cfg = Config;
        if (!cfg.HasSystemEvent("fate")) return;

        var now = NowMs();
        var current = new Dictionary<uint, ushort>();
        foreach (var fate in svc.FateTable)
            if (svc.FateTable.IsValid(fate)) current[fate.FateId] = fate.Progress;

        if (current.Count != lastFates.Count || current.Any(kv => !lastFates.TryGetValue(kv.Key, out var p) || p != kv.Value))
        {
            if (ThrottleOk(EventTypes.FateUpdate, now))
            {
                Add(EventTypes.FateUpdate, now, new
                {
                    added = current.Keys.Except(lastFates.Keys).ToArray(),
                    removed = lastFates.Keys.Except(current.Keys).ToArray(),
                    fates = current.Select(kv => new { fateId = kv.Key, progress = kv.Value }).ToArray(),
                });
            }

            lastFates = current;
        }
    }

    private bool SafeDutyStarted()
    {
        try { return svc.DutyState?.IsDutyStarted ?? false; }
        catch { return false; }
    }

    private HashSet<ulong> NearbyIds(ICharacter player, int range, bool playersOnly)
    {
        var ids = new HashSet<ulong>();
        if (range <= 0) return ids;

        foreach (var obj in svc.ObjectTable)
        {
            if (obj is not ICharacter chara || ReferenceEquals(obj, player)) continue;
            if (playersOnly && obj is not IPlayerCharacter) continue;
            if (!playersOnly && obj is not IBattleNpc) continue;
            if (System.Numerics.Vector3.Distance(player.Position, obj.Position) > range) continue;
            ids.Add(obj.GameObjectId);
        }

        return ids;
    }

    private List<object> NearbyCharacters(ICharacter player, int range, bool playersOnly)
    {
        var list = new List<object>();
        if (range <= 0) return list;

        foreach (var obj in svc.ObjectTable)
        {
            if (obj is not ICharacter chara || ReferenceEquals(obj, player)) continue;
            if (playersOnly && obj is not IPlayerCharacter) continue;
            if (!playersOnly && obj is not IBattleNpc) continue;
            var distance = System.Numerics.Vector3.Distance(player.Position, obj.Position);
            if (distance > range) continue;

            list.Add(new
            {
                id = obj.GameObjectId,
                name = Conv.Str(obj.Name),
                currentHp = chara.CurrentHp,
                maxHp = chara.MaxHp,
                distance = Conv.Round(distance, 1),
            });
        }

        return list;
    }

    private void Add(string type, long now, object data)
    {
        buffer.Add(new EventRecord { Timestamp = now, Type = type, Data = data });
        lastEventTime[type] = now;
    }

    private bool ThrottleOk(string type, long now)
    {
        var gap = Config.ThrottleMs;
        return !lastEventTime.TryGetValue(type, out var last) || now - last >= gap;
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}
