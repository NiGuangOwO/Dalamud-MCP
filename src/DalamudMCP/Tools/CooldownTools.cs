using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Utility;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using LuminaAction = Lumina.Excel.Sheets.Action;
using LuminaClassJob = Lumina.Excel.Sheets.ClassJob;
using LuminaClassJobCategory = Lumina.Excel.Sheets.ClassJobCategory;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Read-only tools that report ability state. Everything here is derived from the game's own
/// Action rows and the ActionManager recast groups, so it can be polled between casts without
/// touching the action path or changing game state.
/// </summary>
internal static unsafe class CooldownTools
{
    /// <summary>
    /// The recast group every GCD action shares. Its members are only blocked for one global
    /// cooldown, so listing them would bury the real cooldowns under a dozen idle weaponskills.
    /// </summary>
    private const int GcdRecastGroup = 58;

    /// <summary>Rows per call, capped so one poll cannot flood a caller's context.</summary>
    private const int MaxRows = 100;

    /// <summary>The recast group slots the client keeps; GetRecastGroup can report no group as -1.</summary>
    private const int RecastGroupCount = 80;

    /// <summary>
    /// Each ClassJobCategory row flags its jobs in a bool column named after the abbreviation, so
    /// the lookup is resolved once per job instead of per action row.
    /// </summary>
    private static readonly ConcurrentDictionary<string, PropertyInfo?> CategoryFlags = new();

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_cooldowns",
            "Get cooldowns",
            "Lists the current job's abilities that are unavailable right now: action id, name, " +
            "recast group, remaining and total recast seconds, and charges. Rows come from the " +
            "Action sheet for the current ClassJob at or below the player's level; actions sharing " +
            "the global cooldown are omitted. The tool only reads recast state and never casts.",
            Json.Schema(),
            _ => GetCooldowns(svc));
    }

    private static JObject GetCooldowns(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");

        if (svc.ObjectTable.LocalPlayer is null)
            throw new ToolException("the local player is not available");

        var manager = ActionManager.Instance();
        if (manager == null)
            throw new ToolException("the action manager is not available yet");

        var jobId = svc.PlayerState.ClassJob.RowId;
        var level = svc.PlayerState.Level;
        var inPvP = svc.ClientState.IsPvP;
        var job = svc.Sheet<LuminaClassJob>().GetRowOrDefault(jobId);
        var jobAbbreviation = job?.Abbreviation.ExtractText();
        var categories = svc.Sheet<LuminaClassJobCategory>();

        var rows = new List<(uint ActionId, string Name, int Group, float Remaining, float Total,
            uint Charges, ushort MaxCharges)>();

        foreach (var action in svc.Sheet<LuminaAction>())
        {
            if (action.IsPvP != inPvP) continue;
            if (!inPvP && (action.ClassJobLevel == 0 || action.ClassJobLevel > level)) continue;
            if (!IsJobAbility(action, jobId, jobAbbreviation, categories)) continue;
            if (action.CooldownGroup == 0) continue;

            // The runtime recast group is authoritative: the sheet column is the starting value,
            // and charge-based or combo actions can be moved to a different group.
            var group = manager->GetRecastGroup((int)ActionType.Action, action.RowId);
            if (group is < 0 or >= RecastGroupCount) continue;
            if (group == GcdRecastGroup) continue;

            var detail = manager->GetRecastGroupDetail(group);
            if (detail == null || !detail->IsActive) continue;

            var remaining = detail->Total - detail->Elapsed;
            if (remaining <= 0) continue;

            rows.Add((action.RowId, action.Name.ExtractText(), group, remaining, detail->Total,
                manager->GetCurrentCharges(action.RowId), ActionManager.GetMaxCharges(action.RowId, 0)));
        }

        rows.Sort(static (left, right) => right.Remaining.CompareTo(left.Remaining));
        var listed = rows.Count > MaxRows ? rows.GetRange(0, MaxRows) : rows;

        var cooldowns = new JArray();
        foreach (var row in listed)
        {
            cooldowns.Add(new JObject
            {
                ["actionId"] = row.ActionId,
                ["name"] = row.Name,
                ["recastGroup"] = row.Group,
                ["remainingSeconds"] = Conv.Round(row.Remaining, 1),
                ["totalRecastSeconds"] = Conv.Round(row.Total, 1),
                ["currentCharges"] = row.Charges,
                ["maxCharges"] = row.MaxCharges,
            });
        }

        var result = new JObject
        {
            ["jobId"] = jobId,
            ["job"] = jobAbbreviation,
            ["level"] = level,
            ["isPvP"] = inPvP,
            ["count"] = cooldowns.Count,
            ["truncated"] = rows.Count > listed.Count,
            ["cooldowns"] = cooldowns,
        };

        if (cooldowns.Count == 0)
            result["note"] = "nothing is on cooldown: every ability this job can use at this level is ready";

        return result;
    }

    /// <summary>
    /// True when the action belongs to the current job: job-bound actions carry the class/job id
    /// directly, while shared role actions leave it unset and flag their jobs in the category row.
    /// </summary>
    private static bool IsJobAbility(LuminaAction action, uint jobId, string? jobAbbreviation,
        ExcelSheet<LuminaClassJobCategory> categories)
    {
        if (action.ClassJob.IsValid && action.ClassJob.RowId == jobId)
            return true;

        if (!action.IsRoleAction || string.IsNullOrEmpty(jobAbbreviation))
            return false;

        var category = categories.GetRowOrDefault(action.ClassJobCategory.RowId);
        if (category is null)
            return false;

        var flag = CategoryFlags.GetOrAdd(jobAbbreviation, static name => typeof(LuminaClassJobCategory).GetProperty(name));

        return flag is not null
            && flag.PropertyType == typeof(bool)
            && flag.GetValue(category.Value) is true;
    }
}
