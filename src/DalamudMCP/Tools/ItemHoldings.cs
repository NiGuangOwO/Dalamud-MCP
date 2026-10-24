using System;
using System.Collections.Generic;
using System.Linq;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal sealed record HoldingItem(uint ItemId, uint BaseItemId, int Quantity, bool IsHq, int Slot);
internal sealed record HoldingContainer(string Scope, string Container, string OwnerId,
    string? OwnerName, bool Available, string? Reason, DateTimeOffset ObservedAt, HoldingItem[] Items);
internal sealed record HoldingRetainer(string Id, string Name);

/// <summary>Bind a ready inventory UI lifetime to its selected retainer; loaded buffers alone have no owner.</summary>
internal sealed class RetainerOwnership
{
    private ulong selected;
    private ulong loadedOwner;
    private bool waiting;
    public void Reset() { selected = 0; loadedOwner = 0; waiting = false; }
    public void Observe(ulong id, bool inventoryReady)
    {
        selected = id;
        if (!inventoryReady)
        {
            loadedOwner = 0;
            waiting = false;
            return;
        }
        if (loadedOwner == 0 && !waiting && id != 0) loadedOwner = id;
        waiting = id == 0 || loadedOwner != id;
    }
    public bool CanRead(ulong id) => id != 0 && id == selected && loadedOwner == id && !waiting;
}

/// <summary>Session-only snapshots. Never combine characters, unavailable containers, or two owners.</summary>
internal sealed class ItemHoldings
{
    internal static readonly string[] Scopes = { "inventory", "equipment", "armoury", "saddlebag", "premiumSaddlebag", "retainer" };
    private readonly Dictionary<string, HoldingContainer> cache = new();
    private ulong character;

    public void SetCharacter(ulong id)
    {
        if (id == character) return;
        cache.Clear();
        character = id;
    }

    public void Observe(IEnumerable<HoldingContainer> containers)
    {
        if (character == 0) return;
        foreach (var c in containers)
            if (c.Available) cache[Key(c)] = c;
        // Real storage fits in 112 entries with ten retainers; cap replacement/invalid-roster churn too.
        while (cache.Count > 128)
            cache.Remove(cache.MinBy(c => c.Value.ObservedAt).Key);
    }

    private static string Key(HoldingContainer c) => c.Scope + ":" + c.OwnerId + ":" + c.Container;

    public JObject Query(JObject args, IReadOnlyList<HoldingContainer> current,
        IReadOnlyList<HoldingRetainer> retainers, bool rosterReady, Func<uint, string?> itemName)
    {
        var query = HoldingQuery.Parse(args);
        var now = DateTimeOffset.UtcNow;
        var live = current.Where(c => query.Scopes.Contains(c.Scope)).ToDictionary(Key);
        var selected = new List<(HoldingContainer Container, bool Cached)>();
        foreach (var c in live.Values)
        {
            if (c.Available) selected.Add((c, false));
            else if (query.IncludeCached && cache.TryGetValue(Key(c), out var previous)) selected.Add((previous, true));
            else selected.Add((c, false));
        }
        if (query.IncludeCached)
            foreach (var c in cache.Values)
                if (query.Scopes.Contains(c.Scope) && !live.ContainsKey(Key(c))) selected.Add((c, true));

        var names = new Dictionary<uint, string?>();
        string? Name(uint id)
        {
            if (!names.TryGetValue(id, out var value)) names[id] = value = itemName(id);
            return value;
        }
        var locations = new JArray();
        var coverage = new JArray();
        long total = 0, nq = 0, hq = 0;
        var matches = 0;
        var unknownNames = false;
        var liveComplete = selected.Count > 0;
        foreach (var (c, cached) in selected.OrderBy(c => c.Container.Scope, StringComparer.Ordinal)
                     .ThenBy(c => c.Container.OwnerId, StringComparer.Ordinal).ThenBy(c => c.Container.Container, StringComparer.Ordinal))
        {
            liveComplete &= c.Available && !cached;
            var source = !c.Available ? "unavailable" : cached ? "cached" : "live";
            long containerTotal = 0;
            foreach (var item in c.Items)
            {
                if (!c.Available) break;
                if (query.ItemId.HasValue && item.BaseItemId != query.ItemId.Value) continue;
                if (query.IsHq.HasValue && item.IsHq != query.IsHq.Value) continue;
                var name = Name(item.BaseItemId);
                if (query.Name is not null)
                {
                    if (name is null) { unknownNames = true; continue; }
                    if (!(query.Contains ? name.Contains(query.Name, StringComparison.OrdinalIgnoreCase)
                        : name.Equals(query.Name, StringComparison.OrdinalIgnoreCase))) continue;
                }
                total += item.Quantity;
                containerTotal += item.Quantity;
                if (item.IsHq) hq += item.Quantity; else nq += item.Quantity;
                if (matches >= query.Offset && locations.Count < query.Limit)
                    locations.Add(new JObject { ["itemId"] = item.ItemId, ["baseItemId"] = item.BaseItemId,
                        ["name"] = name, ["quantity"] = item.Quantity, ["isHq"] = item.IsHq,
                        ["scope"] = c.Scope, ["container"] = c.Container, ["slot"] = item.Slot,
                        ["ownerId"] = c.OwnerId, ["ownerName"] = c.OwnerName, ["source"] = source,
                        ["observedAt"] = c.ObservedAt, ["ageMs"] = Math.Max(0, (long)(now - c.ObservedAt).TotalMilliseconds) });
                matches++;
            }
            coverage.Add(new JObject { ["scope"] = c.Scope, ["container"] = c.Container,
                ["ownerId"] = c.OwnerId, ["ownerName"] = c.OwnerName, ["source"] = source,
                ["reason"] = c.Available ? null : c.Reason, ["observedAt"] = c.Available ? c.ObservedAt : null,
                ["quantity"] = c.Available ? containerTotal : null });
        }
        var retainerCoverage = new JArray();
        if (query.Scopes.Contains("retainer"))
        {
            liveComplete &= rosterReady;
            foreach (var r in retainers)
            {
                var containers = selected.Where(c => c.Container.Scope == "retainer" && c.Container.OwnerId == r.Id).ToArray();
                var ready = containers.Length == 9 && containers.All(c => c.Container.Available);
                var isLive = ready && containers.All(c => !c.Cached);
                liveComplete &= isLive;
                retainerCoverage.Add(new JObject { ["ownerId"] = r.Id, ["ownerName"] = r.Name,
                    ["source"] = isLive ? "live" : ready ? "cached" : "unavailable",
                    ["reason"] = ready ? null : "Visit this retainer's inventory to load all bags, crystals and equipment." });
            }
        }
        liveComplete &= !unknownNames;
        var next = query.Offset + locations.Count;
        return new JObject { ["available"] = character != 0, ["query"] = args.DeepClone(),
            ["totalQuantity"] = total, ["nqQuantity"] = nq, ["hqQuantity"] = hq,
            ["matchedSlots"] = matches, ["count"] = locations.Count, ["offset"] = query.Offset,
            ["nextOffset"] = next < matches ? next : null, ["truncated"] = next < matches,
            ["complete"] = liveComplete, ["unknownItemNames"] = unknownNames,
            ["retainerRosterAvailable"] = rosterReady, ["retainers"] = retainerCoverage,
            ["coverage"] = coverage, ["locations"] = locations,
            ["limitations"] = "Totals cover only the reported live/cached containers; cached values may be stale. " +
                "Unloaded or unvisited storage is unknown, not zero. Snapshots are cleared on logout, character change and plugin reload. " +
                "Premium saddlebags may be unavailable without entitlement. Retainer market listings, glamour dresser, armoire, housing and other characters are excluded." };
    }
}

internal sealed record HoldingQuery(uint? ItemId, string? Name, bool Contains, bool? IsHq,
    HashSet<string> Scopes, bool IncludeCached, int Offset, int Limit)
{
    public static HoldingQuery Parse(JObject args)
    {
        uint? id = null;
        if (args.TryGetValue("itemId", out var token))
        {
            if (token.Type != JTokenType.Integer || !uint.TryParse(token.ToString(), out var value) || value == 0)
                throw new ToolException("itemId must be a positive base item ID integer.");
            id = value;
        }
        string? name = null;
        if (args.TryGetValue("name", out token))
        {
            if (token.Type != JTokenType.String || string.IsNullOrWhiteSpace((string?)token) || token.Value<string>()!.Length > 128)
                throw new ToolException("name must contain 1-128 characters.");
            name = token.Value<string>()!.Trim();
        }
        if (id.HasValue == (name is not null)) throw new ToolException("Provide exactly one of itemId or name.");
        var scopes = new HashSet<string>(ItemHoldings.Scopes, StringComparer.Ordinal);
        if (args.TryGetValue("scopes", out token))
        {
            if (token is not JArray array || array.Count is < 1 or > 6 ||
                array.Any(v => v.Type != JTokenType.String || !scopes.Contains(v.Value<string>()!)))
                throw new ToolException("scopes must contain 1-6 supported scope names.");
            scopes = new HashSet<string>(array.Values<string>()!, StringComparer.Ordinal);
        }
        bool Boolean(string key, bool fallback)
        {
            if (!args.TryGetValue(key, out var t)) return fallback;
            if (t.Type != JTokenType.Boolean) throw new ToolException(key + " must be a JSON boolean.");
            return t.Value<bool>();
        }
        int Integer(string key, int fallback, int min, int max)
        {
            if (!args.TryGetValue(key, out var t)) return fallback;
            if (t.Type != JTokenType.Integer || !int.TryParse(t.ToString(), out var v) || v < min || v > max)
                throw new ToolException($"{key} must be an integer in {min}-{max}.");
            return v;
        }
        return new HoldingQuery(id, name, Boolean("contains", false),
            args.ContainsKey("isHq") ? Boolean("isHq", false) : null, scopes,
            Boolean("includeCached", true), Integer("offset", 0, 0, 10000), Integer("maxResults", 100, 1, 200));
    }
}
