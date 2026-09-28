using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game.ClientState.Aetherytes;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Teleports the player using the game's own aetheryte list. Destination matching is
/// punctuation- and case-insensitive so names typed from a guide ("limsa", "The Gold Saucer")
/// resolve without the caller knowing the exact sheet spelling.
/// </summary>
internal static unsafe class TeleportTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "teleport_to_aetheryte",
            "Teleport to aetheryte",
            "Teleports to an unlocked aetheryte by id or by name. Names are matched loosely: " +
            "case, punctuation, and leading articles are ignored, and a bare zone name matches its " +
            "main city aetheryte. Costs the same gil the in-game teleport menu would charge.",
            Json.Schema(
                ("aetheryteId", "integer", "Aetheryte row id from get_aetherytes (alternative to name)", false),
                ("name", "string", "Aetheryte or territory name, e.g. 'Limsa Lominsa' or 'Gridania'", false),
                ("territoryId", "integer", "Restrict the name match to one territory", false),
                ("subIndex", "integer", "Estate/apartment sub-index when the destination has several", false)),
            args => Teleport(svc, args),
            mutating: true);
    }

    private static JObject Teleport(GameServices svc, JObject args)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");

        var telepo = Telepo.Instance();
        if (telepo is null)
            throw new ToolException("Telepo is not available");

        // The client rebuilds this list from the current unlock state; without the refresh a
        // teleport made right after login would match against an empty list.
        telepo->UpdateAetheryteList();

        var entries = new List<IAetheryteEntry>();
        var list = svc.AetheryteList;
        for (var i = 0; i < list.Length; i++)
        {
            var entry = list[i];
            if (entry is not null) entries.Add(entry);
        }

        if (entries.Count == 0)
            throw new ToolException("the aetheryte list is empty; the character may not be fully loaded yet");

        var requestedId = args.Value<uint?>("aetheryteId");
        var requestedTerritory = args.Value<uint?>("territoryId");
        var requestedName = args.Value<string>("name")?.Trim();
        var requestedSubIndex = args.Value<byte?>("subIndex");

        IAetheryteEntry destination;
        if (requestedId is not null)
        {
            var matches = entries.Where(e => e.AetheryteId == requestedId.Value).ToList();
            if (requestedTerritory is not null)
                matches = matches.Where(e => e.TerritoryId == requestedTerritory.Value).ToList();
            if (requestedSubIndex is not null)
            {
                var exact = matches.Where(e => e.SubIndex == requestedSubIndex.Value).ToList();
                if (exact.Count > 0) matches = exact;
            }

            if (matches.Count == 0)
                throw new ToolException($"no unlocked aetheryte with id {requestedId.Value}");
            destination = matches[0];
        }
        else if (!string.IsNullOrWhiteSpace(requestedName))
        {
            var candidates = entries.AsEnumerable();
            if (requestedTerritory is not null)
            {
                var scoped = entries.Where(e => e.TerritoryId == requestedTerritory.Value).ToList();
                if (scoped.Count > 0) candidates = scoped;
            }

            var ranked = candidates
                .Select(e => (Entry: e, Score: Score(e, requestedName)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ToList();

            if (ranked.Count == 0)
                throw new ToolException(
                    $"no unlocked aetheryte matches '{requestedName}'. Call get_aetherytes to list the destinations available to this character.");

            var best = ranked[0].Score;
            var top = ranked.Where(x => x.Score == best).ToList();
            if (top.Count > 1 && requestedTerritory is null)
            {
                var names = string.Join(", ", top.Take(6).Select(t => Describe(t.Entry)));
                throw new ToolException(
                    $"'{requestedName}' matches more than one destination ({names}); pass territoryId or aetheryteId to disambiguate.");
            }

            destination = ranked[0].Entry;
        }
        else
        {
            throw new ToolException("provide aetheryteId or name");
        }

        var subIndex = requestedSubIndex ?? destination.SubIndex;
        var ok = telepo->Teleport(destination.AetheryteId, subIndex);

        return new JObject
        {
            ["success"] = ok,
            ["aetheryteId"] = destination.AetheryteId,
            ["subIndex"] = subIndex,
            ["name"] = Name(destination),
            ["territoryId"] = destination.TerritoryId,
            ["gilCost"] = destination.GilCost,
            ["activeTeleportRequest"] = telepo->ActiveTeleportRequest,
        };
    }

    /// <summary>
    /// Ranks how well an entry answers the requested text. Higher is better; 0 means no match at
    /// all. Territory names score below place names so "Limsa Lominsa" prefers the city aetheryte
    /// over an aethernet shard that merely sits in that territory.
    /// </summary>
    private static int Score(IAetheryteEntry entry, string query)
    {
        var wanted = Normalize(query);
        if (wanted.Length == 0) return 0;

        var place = Normalize(Name(entry));
        var aethernet = Normalize(EntryText(entry.AetheryteData, r => r.AethernetName.Value.Name.ExtractText()));
        var territory = Normalize(EntryText(entry.AetheryteData, r => r.Territory.Value.PlaceName.Value.Name.ExtractText()));

        if (place == wanted) return 100;
        if (aethernet == wanted) return 90;
        if (territory == wanted) return 80;
        if (place.StartsWith(wanted, StringComparison.Ordinal)) return 70;
        if (aethernet.StartsWith(wanted, StringComparison.Ordinal)) return 60;
        if (territory.StartsWith(wanted, StringComparison.Ordinal)) return 50;
        if (place.Contains(wanted, StringComparison.Ordinal)) return 40;
        if (territory.Contains(wanted, StringComparison.Ordinal)) return 30;
        if (aethernet.Contains(wanted, StringComparison.Ordinal)) return 20;
        return 0;
    }

    private static string Describe(IAetheryteEntry entry)
    {
        var territory = EntryText(entry.AetheryteData, r => r.Territory.Value.PlaceName.Value.Name.ExtractText());
        return string.IsNullOrEmpty(territory) ? Name(entry) : $"{Name(entry)} ({territory})";
    }

    private static string Name(IAetheryteEntry entry) =>
        EntryText(entry.AetheryteData, r => r.PlaceName.Value.Name.ExtractText()) ?? string.Empty;

    /// <summary>
    /// Reads a field from the entry's aetheryte row. The RowRef is not always resolved (the row
    /// can be missing for a territory the client has not installed), so this never lets a
    /// malformed row take down the whole lookup.
    /// </summary>
    private static string? EntryText(Lumina.Excel.RowRef<Lumina.Excel.Sheets.Aetheryte> row, Func<Lumina.Excel.Sheets.Aetheryte, string?> selector)
    {
        try
        {
            if (!row.IsValid) return null;
            return selector(row.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Lower-cases and drops everything that is not a letter or digit.</summary>
    private static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }
}
