using System;
using Dalamud.Game.ClientState.Buddy;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Companion (chocobo) / pet / battle-buddy state and duty progress. These need the
/// IBuddyList and IDutyState services, which the object/party tools do not cover.
/// </summary>
internal static class BuddyTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "get_buddy_list",
            "Get buddy list",
            "Companion (chocobo), pet and trust buddies currently following the player, with HP, level " +
            "and stance where available.",
            Json.Schema(),
            _ => BuddyList(svc));

        registry.Add(
            "get_duty_state",
            "Get duty state",
            "Whether a duty (dungeon, raid, trial, etc.) is currently running, plus its content finder " +
            "name when known.",
            Json.Schema(),
            _ => DutyState(svc));
    }

    private static JObject BuddyList(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return NotAvailable;

        var buddies = new JArray();
        var buddyList = svc.BuddyList;
        if (buddyList is not null)
        {
            for (var i = 0; i < buddyList.Length; i++)
            {
                var member = buddyList[i];
                if (member is null) continue;
                var gameObject = SafeGameObject(member);
                buddies.Add(new JObject
                {
                    ["slot"] = i,
                    ["name"] = gameObject is null ? null : Conv.Str(gameObject.Name),
                    ["currentHp"] = member.CurrentHP,
                    ["maxHp"] = member.MaxHP,
                    ["entityId"] = member.EntityId,
                    ["dataId"] = member.DataID,
                    ["gameObjectId"] = gameObject?.GameObjectId,
                    ["level"] = gameObject is Dalamud.Game.ClientState.Objects.Types.ICharacter ch ? ch.Level : null,
                });
            }
        }

        return new JObject
        {
            ["count"] = buddies.Count,
            ["buddies"] = buddies,
        };
    }

    private static Dalamud.Game.ClientState.Objects.Types.IGameObject? SafeGameObject(IBuddyMember member)
    {
        try
        {
            return member.GameObject;
        }
        catch
        {
            return null;
        }
    }

    private static JObject DutyState(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            return NotAvailable;

        var dutyState = svc.DutyState;
        var started = dutyState?.IsDutyStarted ?? false;

        var result = new JObject
        {
            ["isDutyStarted"] = started,
        };

        if (started)
        {
            try
            {
                var cfc = dutyState!.ContentFinderCondition;
                result["contentFinderCondition"] = new JObject
                {
                    ["rowId"] = cfc.RowId,
                    ["name"] = cfc.IsValid ? cfc.Value.Name.ExtractText() : null,
                };
            }
            catch
            {
                // ContentFinderCondition can be unavailable mid-transition; keep the
                // duty flag itself authoritative.
            }
        }

        return result;
    }

    private static JObject NotAvailable => new()
    {
        ["available"] = false,
        ["reason"] = "not logged in",
    };
}
