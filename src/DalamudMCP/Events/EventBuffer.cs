using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DalamudMCP.Events;

/// <summary>One recorded game event: a millisecond timestamp, a type tag, and a payload.</summary>
public sealed class EventRecord
{
    public long Id { get; set; }

    public long Timestamp { get; set; }

    public string Type { get; set; } = string.Empty;

    public object? Data { get; set; }
}

/// <summary>Canonical event type tags stored on <see cref="EventRecord.Type"/>.</summary>
public static class EventTypes
{
    public const string HpChange = "hp_change";
    public const string MpChange = "mp_change";
    public const string GpChange = "gp_change";
    public const string PlayerMove = "player_move";
    public const string JobChange = "job_change";
    public const string TargetChange = "target_change";
    public const string FocusTargetChange = "focus_target_change";
    public const string TargetHpChange = "target_hp_change";
    public const string CombatDamage = "combat_damage";
    public const string CombatStart = "combat_start";
    public const string CombatEnd = "combat_end";
    public const string MapChange = "map_change";
    public const string MountChange = "mount_change";
    public const string DutyUpdate = "duty_update";
    public const string FateUpdate = "fate_update";
    public const string NearbyEnemy = "nearby_enemy";
    public const string NearbyPlayer = "nearby_player";
}

/// <summary>
/// What the collector watches each frame. Enabled categories are lists of whitelisted
/// keywords so a future event source can be added without a config schema change.
/// </summary>
public sealed class EventCollectionConfig
{
    public List<string> PlayerStats { get; set; } = new() { "hp", "mp", "gp", "job", "position" };

    public List<string> TargetStats { get; set; } = new() { "hp", "type", "targetChange" };

    /// <summary>Scan radius (yalms) for enemy change detection; 0 disables it.</summary>
    public int ObjectRange { get; set; } = 30;

    public List<string> ObjectTypes { get; set; } = new() { "enemy" };

    /// <summary>Scan radius for nearby player detection; 0 (default) disables it.</summary>
    public int NearbyPlayerRange { get; set; }

    public List<string> CombatEvents { get; set; } = new() { "damage", "startEnd" };

    public List<string> SystemEvents { get; set; } = new() { "duty", "fate" };

    /// <summary>Minimum gap between two records of the same event type.</summary>
    public int ThrottleMs { get; set; } = 500;

    public bool HasPlayerStat(string stat) => PlayerStats.Contains(stat);

    public bool HasTargetStat(string stat) => TargetStats.Contains(stat);

    public bool HasObjectType(string type) => ObjectTypes.Contains(type);

    public bool HasCombatEvent(string evt) => CombatEvents.Contains(evt);

    public bool HasSystemEvent(string evt) => SystemEvents.Contains(evt);

    /// <summary>Strict validation for values arriving from an MCP call: unknown keys or
    /// out-of-range numbers fail with an explanatory error.</summary>
    public void Validate()
    {
        ValidateValues(PlayerStats, new[] { "hp", "mp", "gp", "job", "position" }, "playerStats");
        ValidateValues(TargetStats, new[] { "hp", "type", "targetChange" }, "targetStats");
        ValidateValues(ObjectTypes, new[] { "enemy" }, "objectTypes");
        ValidateValues(CombatEvents, new[] { "damage", "startEnd" }, "combatEvents");
        ValidateValues(SystemEvents, new[] { "duty", "fate" }, "systemEvents");
        if (ObjectRange is < 0 or > 200)
            throw new ArgumentException("objectRange must be between 0 and 200");
        if (NearbyPlayerRange is < 0 or > 200)
            throw new ArgumentException("nearbyPlayerRange must be between 0 and 200");
        if (ThrottleMs is < 50 or > 60000)
            throw new ArgumentException("throttleMs must be between 50 and 60000");
    }

    /// <summary>Lenient clamp used when loading persisted config: bad values are repaired
    /// instead of rejected so a hand-edited config file cannot break startup.</summary>
    public void Clamp()
    {
        PlayerStats = Filter(PlayerStats, new[] { "hp", "mp", "gp", "job", "position" },
            PlayerStats.Count == 0 ? new List<string> { "hp", "mp", "gp", "job", "position" } : new List<string>());
        TargetStats = Filter(TargetStats, new[] { "hp", "type", "targetChange" },
            TargetStats.Count == 0 ? new List<string> { "hp", "type", "targetChange" } : new List<string>());
        ObjectTypes = Filter(ObjectTypes, new[] { "enemy" }, new List<string> { "enemy" });
        CombatEvents = Filter(CombatEvents, new[] { "damage", "startEnd" }, new List<string> { "damage", "startEnd" });
        SystemEvents = Filter(SystemEvents, new[] { "duty", "fate" }, new List<string> { "duty", "fate" });
        ObjectRange = Math.Clamp(ObjectRange, 0, 200);
        NearbyPlayerRange = Math.Clamp(NearbyPlayerRange, 0, 200);
        ThrottleMs = Math.Clamp(ThrottleMs, 50, 60000);
    }

    private static List<string> Filter(IEnumerable<string> values, string[] allowed, List<string> whenEmpty)
    {
        var kept = values.Where(allowed.Contains).Distinct().ToList();
        return kept.Count > 0 ? kept : whenEmpty;
    }

    private static void ValidateValues(IEnumerable<string> values, IReadOnlyCollection<string> allowed, string name)
    {
        var invalid = values.Where(v => !allowed.Contains(v)).Distinct().ToArray();
        if (invalid.Length > 0)
            throw new ArgumentException($"{name} contains unsupported values: {string.Join(", ", invalid)}");
    }
}

/// <summary>Outcome of a bounded wait on the event buffer.</summary>
public readonly struct EventWaitResult
{
    public EventWaitResult(List<EventRecord> events, bool timedOut)
    {
        Events = events;
        TimedOut = timedOut;
    }

    public List<EventRecord> Events { get; }

    /// <summary>True when the deadline passed without a matching event arriving.</summary>
    public bool TimedOut { get; }
}

/// <summary>Fixed-capacity FIFO of events; oldest entries are dropped once full.</summary>
public sealed class EventBuffer
{
    private readonly ConcurrentQueue<EventRecord> queue = new();
    private readonly int maxSize;

    // Monotonic id source and the wake-up latch used by WaitAsync. Assignments happen
    // under <see cref="sync"/> so a waiter that captures the latch before re-scanning can
    // never miss a concurrently appended record.
    private readonly object sync = new();
    private TaskCompletionSource latch = NewLatch();
    private long nextId;

    public EventBuffer(int maxSize = 2000) => this.maxSize = maxSize;

    /// <summary>Highest id handed out so far. Callers pass it back as a cursor.</summary>
    public long LastId
    {
        get
        {
            lock (sync) return nextId;
        }
    }

    public void Add(EventRecord record)
    {
        lock (sync)
        {
            record.Id = ++nextId;
            queue.Enqueue(record);
            while (queue.Count > maxSize) queue.TryDequeue(out _);

            // Release everyone currently waiting, then arm a fresh latch for the next wait.
            var previous = latch;
            latch = NewLatch();
            previous.TrySetResult();
        }
    }

    /// <summary>
    /// Returns the oldest-first slice of records with <c>Id &gt; afterId</c>, capped at
    /// <paramref name="count"/>. Ordering matches how a reader consumes a stream.
    /// </summary>
    public List<EventRecord> QueryAfter(long afterId, string[]? types = null, int count = 100)
    {
        var result = new List<EventRecord>();
        foreach (var e in queue)
        {
            if (e.Id <= afterId) continue;
            if (types is { Length: > 0 } && !types.Contains(e.Type)) continue;
            result.Add(e);
        }

        if (count > 0 && result.Count > count)
            result.RemoveRange(count, result.Count - count);
        return result;
    }

    /// <summary>
    /// Completes as soon as a matching record is appended, or reports a timeout once
    /// <paramref name="timeout"/> elapses. Deliberately free of framework-thread work: it is
    /// driven from a thread-pool continuation so a long wait never stalls the game loop.
    /// </summary>
    public async Task<EventWaitResult> WaitAsync(
        long afterId,
        string[]? types,
        int count,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (true)
        {
            // Capture the latch *before* scanning: an append that lands in between then
            // satisfies the latch we already hold, so no wake-up can be lost.
            Task notification;
            lock (sync) notification = latch.Task;

            var found = QueryAfter(afterId, types, count);
            if (found.Count > 0) return new EventWaitResult(found, timedOut: false);

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
                return new EventWaitResult(QueryAfter(afterId, types, count), timedOut: true);

            var delay = Task.Delay(remaining, cancellationToken);
            var completed = await Task.WhenAny(notification, delay).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (ReferenceEquals(completed, delay))
            {
                var last = QueryAfter(afterId, types, count);
                return new EventWaitResult(last, timedOut: last.Count == 0);
            }
        }
    }

    private static TaskCompletionSource NewLatch() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Returns up to <paramref name="count"/> newest matching records (newest first).</summary>
    public List<EventRecord> Query(string[]? types = null, int count = 50, long? since = null, long? before = null)
    {
        var filtered = new List<EventRecord>();
        foreach (var e in queue)
        {
            if (types is { Length: > 0 } && !types.Contains(e.Type)) continue;
            if (since.HasValue && e.Timestamp < since.Value) continue;
            if (before.HasValue && e.Timestamp > before.Value) continue;
            filtered.Add(e);
        }

        filtered.Reverse();
        if (count > 0 && filtered.Count > count)
            filtered.RemoveRange(count, filtered.Count - count);
        return filtered;
    }

    public int Count => queue.Count;

    public int MaxSize => maxSize;
}
