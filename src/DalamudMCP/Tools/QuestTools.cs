using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Dalamud.Game;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Quest bookkeeping: whether the character has a quest, which step it is on, and which
/// quests are standing in the world waiting to be taken. The accepted/completed answers come
/// from the game's own quest manager rather than a sheet column, so they match the journal.
/// </summary>
internal static unsafe class QuestTools
{
    private const int MaxResultsLimit = 20;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_quest_status",
            "Get quest status",
            "Reports whether one quest is accepted or complete and which sequence step it is on, " +
            "looked up by quest id or by (partial) name. Searches every installed client language, " +
            "so a quest can be found by its English name even on a non-English client.",
            Json.Schema(
                ("questId", "integer", "Quest row id (alternative to query)", false),
                ("query", "string", "Quest name, or part of it (alternative to questId)", false),
                ("maxResults", "integer", "How many name matches to report, 1-20 (default 8)", false)),
            args => Status(svc, args));

        registry.Add(
            "get_available_quests",
            "Get available quests",
            "Lists quests that are currently offered in the world but not yet accepted, with the " +
            "map marker position, level and objective id. Use nameContains to narrow a busy zone.",
            Json.Schema(
                ("nameContains", "string", "Only report quests whose name contains this text", false),
                ("maxResults", "integer", "How many quests to report, 1-20 (default 8)", false)),
            args => Available(svc, args));
    }

    // ---------------------------------------------------------------- quest status

    private static JObject Status(GameServices svc, JObject args)
    {
        var questId = args.Value<uint?>("questId");
        var query = args.Value<string>("query")?.Trim();
        var maxResults = Math.Clamp(args.Value<int?>("maxResults") ?? 8, 1, MaxResultsLimit);

        if (questId is null && string.IsNullOrWhiteSpace(query))
            throw new ToolException("provide either questId or query");

        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");

        var manager = QuestManager.Instance();
        var uiState = UIState.Instance();
        if (manager is null || uiState is null)
            throw new ToolException("the quest systems are not available");

        var matches = new List<Quest>();
        if (questId is not null)
        {
            var row = svc.Sheet<Quest>().GetRowOrDefault(questId.Value);
            if (row is null || row.Value.RowId == 0)
                throw new ToolException($"no quest with id {questId.Value}");
            matches.Add(row.Value);
        }
        else
        {
            matches = FindByName(svc, query!, maxResults);
            if (matches.Count == 0)
                throw new ToolException($"no quest name contains '{query}'. Names are matched against every installed client language.");
        }

        var quests = new JArray();
        foreach (var row in matches.Take(maxResults))
            quests.Add(Describe(manager, uiState, row));

        return new JObject
        {
            ["count"] = quests.Count,
            ["quests"] = quests,
        };
    }

    private static JObject Describe(QuestManager* manager, UIState* uiState, Quest row)
    {
        var id = row.RowId;
        var accepted = manager->IsQuestAccepted(id);
        var completed = uiState->IsUnlockLinkUnlockedOrQuestCompleted(id, 0, true);

        var entry = new JObject
        {
            ["questId"] = id,
            ["name"] = row.Name.ExtractText(),
            ["isAccepted"] = accepted,
            ["isCompleted"] = completed,
            ["sequence"] = accepted ? QuestManager.GetQuestSequence(id) : null,
        };

        if (QuestLevel(row) is { } level) entry["questLevel"] = level;
        if (row.PlaceName.IsValid) entry["placeName"] = row.PlaceName.Value.Name.ExtractText();
        if (row.Expansion.IsValid) entry["expansion"] = row.Expansion.Value.Name.ExtractText();
        if (row.JournalGenre.IsValid) entry["journalGenre"] = row.JournalGenre.Value.Name.ExtractText();

        return entry;
    }

    /// <summary>
    /// The quest's own level. The sheet exposes a per-job level list plus a plain cap; the first
    /// list entry is what the journal shows, and the cap is the fallback when the list is empty.
    /// </summary>
    private static int? QuestLevel(Quest row)
    {
        try
        {
            var levels = row.ClassJobLevel;
            if (levels.Count > 0 && levels[0] > 0) return levels[0];
        }
        catch (Exception)
        {
            // A malformed column should not fail the whole response.
        }

        try
        {
            if (row.LevelMax > 0) return row.LevelMax;
        }
        catch (Exception)
        {
        }

        return null;
    }

    // ------------------------------------------------------------- available quests

    private static JObject Available(GameServices svc, JObject args)
    {
        var nameContains = args.Value<string>("nameContains")?.Trim();
        var maxResults = Math.Clamp(args.Value<int?>("maxResults") ?? 8, 1, MaxResultsLimit);

        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");

        var map = FFXIVClientStructs.FFXIV.Client.Game.UI.Map.Instance();
        var manager = QuestManager.Instance();
        var uiState = UIState.Instance();
        if (map is null || manager is null || uiState is null)
            throw new ToolException("the quest marker systems are not available");

        var sheet = svc.Sheet<Quest>();
        var seen = new HashSet<uint>();
        var quests = new JArray();

        foreach (var marker in map->UnacceptedQuestMarkers)
        {
            if (quests.Count >= maxResults) break;

            var questId = marker.ObjectiveId;
            if (questId == 0 || !marker.ShouldRender) continue;
            if (!seen.Add(questId)) continue;
            if (manager->IsQuestAccepted(questId)) continue;
            if (uiState->IsUnlockLinkUnlockedOrQuestCompleted(questId, 0, true)) continue;

            var row = sheet.GetRowOrDefault(questId);
            var name = row?.Name.ExtractText();
            if (string.IsNullOrWhiteSpace(name)) name = SafeLabel(marker);

            if (!string.IsNullOrEmpty(nameContains) &&
                (name is null || !name.Contains(nameContains, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var entry = new JObject
            {
                ["questId"] = questId,
                ["name"] = name,
            };

            if (row is { } resolved && QuestLevel(resolved) is { } level) entry["questLevel"] = level;
            if (marker.RecommendedLevel > 0) entry["recommendedLevel"] = marker.RecommendedLevel;

            var markerJson = FirstMarker(marker);
            if (markerJson is not null) entry["marker"] = markerJson;

            quests.Add(entry);
        }

        return new JObject
        {
            ["territoryId"] = svc.ClientState.TerritoryType,
            ["nameContains"] = Conv.NullIfEmpty(nameContains),
            ["count"] = quests.Count,
            ["quests"] = quests,
        };
    }

    /// <summary>
    /// The first map marker for a quest. A marker carries a list of positions; the first one is
    /// the visible offer location, and the rest are usually quest-step follow-ups.
    /// </summary>
    private static JObject? FirstMarker(MarkerInfo marker)
    {
        try
        {
            var span = marker.MarkerData.AsSpan();
            if (span.Length == 0) return null;

            var data = span[0];
            return new JObject
            {
                ["dataId"] = data.DataId,
                ["mapId"] = data.MapId,
                ["territoryTypeId"] = data.TerritoryTypeId,
                ["radius"] = Conv.Round(data.Radius, 1),
                ["position"] = Conv.Vec3(data.Position),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? SafeLabel(MarkerInfo marker)
    {
        try
        {
            var label = marker.Label.ToString();
            return string.IsNullOrWhiteSpace(label) ? null : label;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------- name search

    /// <summary>
    /// Finds quests whose name contains <paramref name="query"/>. The query and the names are
    /// both reduced to letters and digits so punctuation and spacing in a guide's spelling do
    /// not matter. The client's own language is searched first, then the other installed ones.
    /// </summary>
    private static List<Quest> FindByName(GameServices svc, string query, int maxResults)
    {
        var wanted = Normalize(query);
        if (wanted.Length == 0) return new List<Quest>();

        var found = new List<Quest>();
        var seen = new HashSet<uint>();

        foreach (var language in LanguageOrder(svc))
        {
            if (found.Count >= maxResults) break;

            Lumina.Excel.ExcelSheet<Quest> sheet;
            try
            {
                sheet = svc.DataManager.GetExcelSheet<Quest>(language);
            }
            catch (Exception)
            {
                // That language is not installed in this client.
                continue;
            }

            foreach (var row in sheet)
            {
                if (found.Count >= maxResults) break;

                var name = row.Name.ExtractText();
                if (string.IsNullOrWhiteSpace(name)) continue;
                if (!Normalize(name).Contains(wanted, StringComparison.Ordinal)) continue;
                if (!seen.Add(row.RowId)) continue;

                found.Add(row);
            }
        }

        return found;
    }

    private static IEnumerable<ClientLanguage> LanguageOrder(GameServices svc)
    {
        var current = svc.ClientState.ClientLanguage;
        yield return current;

        foreach (var language in new[]
                 {
                     ClientLanguage.English,
                     ClientLanguage.Japanese,
                     ClientLanguage.German,
                     ClientLanguage.French,
                 })
        {
            if (language != current) yield return language;
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
