using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DalamudMCP.Ipc;

/// <summary>One pushed payload awaiting retrieval.</summary>
public sealed class PushDataEntry
{
    public long Id { get; set; }

    public string Key { get; set; } = string.Empty;

    public string JsonData { get; set; } = string.Empty;

    public long Timestamp { get; set; }
}

/// <summary>
/// A named, retained stream carved out of the push channel. Payloads tagged with the
/// subscription's key are mirrored into its own queue as they arrive, so a client can
/// drain them incrementally instead of racing the rolling global buffer.
/// </summary>
public sealed class PushDataSubscription
{
    public string Key { get; init; } = string.Empty;

    public int Capacity { get; init; }

    /// <summary>Entries waiting to be drained, oldest first.</summary>
    public Queue<PushDataEntry> Pending { get; } = new();

    /// <summary>Id of the most recent entry handed to a poll.</summary>
    public long Cursor { get; set; }

    /// <summary>Payloads evicted by the capacity cap before any poll took them.</summary>
    public long Dropped { get; set; }

    /// <summary>Payloads accepted since the subscription was created.</summary>
    public long Received { get; set; }

    public long Created { get; init; }
}

/// <summary>
/// Fixed-capacity queue that other plugins fill through the IPC provider and MCP
/// clients drain through <c>query_push_data</c>, plus the opt-in per-key subscriptions
/// that <c>plugin_data_subscribe</c> / <c>plugin_data_poll</c> / <c>plugin_data_unsubscribe</c>
/// drive.
/// </summary>
public sealed class PushDataBuffer
{
    private readonly ConcurrentQueue<PushDataEntry> queue = new();
    private readonly Dictionary<string, PushDataSubscription> subscriptions = new(StringComparer.Ordinal);
    private readonly object subscriptionLock = new();
    private readonly int maxSize;
    private long nextId;

    public PushDataBuffer(int maxSize = 2000) => this.maxSize = maxSize;

    public PushDataEntry Push(string key, string jsonData)
    {
        var entry = new PushDataEntry
        {
            Id = Interlocked.Increment(ref nextId),
            Key = key,
            JsonData = jsonData,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        queue.Enqueue(entry);
        while (queue.Count > maxSize) queue.TryDequeue(out _);

        lock (subscriptionLock)
        {
            if (subscriptions.TryGetValue(key, out var subscription))
            {
                subscription.Pending.Enqueue(entry);
                subscription.Received++;
                while (subscription.Pending.Count > subscription.Capacity)
                {
                    subscription.Pending.Dequeue();
                    subscription.Dropped++;
                }
            }
        }

        return entry;
    }

    /// <summary>Returns up to <paramref name="count"/> newest entries (newest first).</summary>
    public List<PushDataEntry> Query(string? key = null, int count = 50)
    {
        var filtered = queue
            .Where(e => string.IsNullOrEmpty(key) || e.Key == key)
            .Reverse()
            .Take(Math.Max(0, count))
            .ToList();
        return filtered;
    }

    /// <summary>
    /// Starts retaining payloads for <paramref name="key"/>. Repeated calls for the same
    /// key keep the existing queue rather than duplicating it.
    /// </summary>
    public PushDataSubscription Subscribe(string key, int capacity)
    {
        lock (subscriptionLock)
        {
            if (subscriptions.TryGetValue(key, out var existing)) return existing;

            var subscription = new PushDataSubscription
            {
                Key = key,
                Capacity = capacity,
                Cursor = Interlocked.Read(ref nextId),
                Created = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            subscriptions[key] = subscription;
            return subscription;
        }
    }

    /// <summary>Stops retaining payloads for <paramref name="key"/>, discarding what is queued.</summary>
    public bool Unsubscribe(string key)
    {
        lock (subscriptionLock)
        {
            return subscriptions.Remove(key);
        }
    }

    public PushDataSubscription? Find(string key)
    {
        lock (subscriptionLock)
        {
            return subscriptions.TryGetValue(key, out var subscription) ? subscription : null;
        }
    }

    public List<PushDataSubscription> Subscriptions()
    {
        lock (subscriptionLock)
        {
            return subscriptions.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
        }
    }

    /// <summary>
    /// Drains up to <paramref name="maxItems"/> retained entries for <paramref name="key"/>,
    /// oldest first. Drained entries are removed; whatever is left stays for the next poll.
    /// </summary>
    public List<PushDataEntry> Poll(string key, int maxItems)
    {
        var drained = new List<PushDataEntry>();
        lock (subscriptionLock)
        {
            if (!subscriptions.TryGetValue(key, out var subscription)) return drained;

            while (drained.Count < maxItems && subscription.Pending.Count > 0)
            {
                var entry = subscription.Pending.Dequeue();
                subscription.Cursor = entry.Id;
                drained.Add(entry);
            }
        }

        return drained;
    }

    public int Count => queue.Count;

    public int MaxSize => maxSize;
}
