using System;
using System.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Application.Network.WorkDefinitions;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

internal static unsafe class TribalQuestTools
{
    internal static uint? ReadAllowance(QuestManager* manager, out string? error)
    {
        error = null;
        try { return manager->GetBeastTribeAllowance(); }
        catch (InvalidOperationException ex) { error = ex.Message; return null; }
    }

    public static void Register(ToolRegistry registry, GameServices svc) => registry.Add(
        "get_tribal_quest_state", "Read tribal reputation and daily allowance",
        "Reads the installed SDK quest manager's remaining tribal allowance, all named BeastTribe sheet rows, " +
        "guarded reputation work records and daily quest slots. Reports raw rank/value and sheet maximum; unknown ranks " +
        "remain unknown. Does not accept quests, advance rank or call third-party plugins. Read-only.",
        Json.Schema(), _ => Read(svc));

    private static object Read(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn) throw new ToolException("not logged in");
        var manager = QuestManager.Instance();
        if (manager == null) throw new ToolException("quest manager unavailable");
        var state = MemoryProbe.TryReadStruct<QuestManager>((ulong)manager, out var error);
        if (!state.HasValue) throw new ToolException("quest manager unreadable: " + error);
        var capacity = state.Value.BeastReputation.Length;
        var tribes = new JArray();
        var ranks = svc.Sheet<BeastReputationRank>();
        foreach (var row in svc.Sheet<BeastTribe>().Where(r => r.RowId > 0 && !r.Name.IsEmpty).Take(64))
        {
            var entry = new JObject { ["tribeId"] = row.RowId, ["name"] = row.Name.ExtractText(),
                ["maxRank"] = row.MaxRank, ["minLevel"] = row.MinLevel, ["expansionId"] = row.Expansion.RowId,
                ["locationLevelId"] = row.Level.RowId, ["currencyItemId"] = row.CurrencyItem.RowId };
            // The SDK accessor handles the network work-record indexing. Do not reuse another patch's offsets.
            var pointer = row.RowId <= capacity ? manager->GetBeastReputationById(row.RowId) : null;
            var work = MemoryProbe.TryReadStruct<BeastReputationWork>((ulong)pointer, out _);
            entry["available"] = work.HasValue;
            if (work.HasValue)
            {
                var rank = work.Value.Rank;
                var rankRow = ranks.GetRowOrDefault(rank);
                entry["rankRaw"] = rank;
                entry["reputation"] = work.Value.Value;
                entry["rankName"] = rankRow?.Name.ExtractText();
                entry["requiredReputation"] = rankRow?.RequiredReputation;
                entry["isUnlocked"] = rank > 0;
                entry["isMaxRank"] = rankRow.HasValue && rankRow.Value.RowId > 0 ? rank >= row.MaxRank : null;
            }
            tribes.Add(entry);
        }
        var daily = new JArray();
        var snapshot = state.Value;
        foreach (var work in snapshot.DailyQuests)
        {
            if (work.QuestId == 0) continue;
            var id = 0x10000U + work.QuestId;
            var row = svc.Sheet<Quest>().GetRowOrDefault(id);
            daily.Add(new JObject { ["questId"] = id, ["name"] = row?.Name.ExtractText(),
                ["flagsRaw"] = work.Flags, ["isAccepted"] = manager->IsQuestAccepted(id),
                ["isDailyCompleted"] = manager->IsDailyQuestCompleted(work.QuestId),
                ["sequence"] = manager->IsQuestAccepted(id) ? QuestManager.GetQuestSequence(id) : null });
        }
        var allowance = ReadAllowance(manager, out var allowanceError);
        return new JObject { ["allowanceRemaining"] = allowance, ["allowanceError"] = allowanceError,
            ["reputationCapacity"] = capacity, ["tribes"] = tribes, ["dailyQuests"] = daily };
    }
}
