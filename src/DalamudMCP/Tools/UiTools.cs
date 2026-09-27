using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;

namespace DalamudMCP.Tools;

/// <summary>
/// The plugin's first mutating tool set: opening and closing the game's addon (UI window)
/// surfaces through the FFXIVClientStructs agent layer.
///
/// <para>Every tool here is registered with <c>mutating: true</c>, so the whole set is invisible
/// to <c>tools/list</c> and refused by <c>tools/call</c> while the <c>AllowMutatingTools</c>
/// configuration switch is off 鈥?the same two-place gate every future state-changing tool
/// inherits. The actions themselves are bounded to what a player could do with a keyboard
/// shortcut: show or hide a system window, or ask which ones are open. Nothing here types into
/// the game, fires addon callbacks, or touches combat state.</para>
///
/// <para>The mechanics: FFXIVClientStructs' <see cref="AgentInterface.Show"/> resolves the
/// singleton agent for an <see cref="AgentId"/> and shows its addon 鈥?the same path the game's
/// own keybinds take, so window placement, focus and state handling stay the client's own.
/// <see cref="AgentInterface.IsAgentActive"/> reports whether the agent currently considers
/// itself active (its window open). Agent lookups go through the static
/// <see cref="AgentModule.Instance"/>; both the module pointer and the per-agent pointer are
/// null-checked rather than trusted, and every failure becomes a structured
/// <c>available=false</c> answer with a reason, never an escaped exception.</para>
/// </summary>
internal static class UiTools
{
    /// <summary>
    /// The agent ids this tool set will open, by name. The full <see cref="AgentId"/> enum has
    /// ~500 members, many of which are context menus or sub-panels that make no sense opened
    /// standalone (or crash when shown out of context); an allowlist keeps the tool's surface
    /// auditable and each entry hand-verifiable in game.
    /// </summary>
    private static readonly Dictionary<string, AgentId> OpenableAgents = new()
    {
        ["inventory"] = AgentId.Inventory,
        ["armoury"] = AgentId.ArmouryBoard,
        ["actionmenu"] = AgentId.ActionMenu,
        ["mirageprism"] = AgentId.MiragePrism,
        ["colorant"] = AgentId.Colorant,
        ["emote"] = AgentId.Emote,
        ["questjournal"] = AgentId.QuestJournal,
        ["journaldetail"] = AgentId.Detail,
        ["fishingnote"] = AgentId.FishingNote,
        ["fishguide"] = AgentId.FishGuide,
        ["monsternote"] = AgentId.MonsterNote,
        ["achievement"] = AgentId.Achievement,
        ["mountnotebook"] = AgentId.MountNotebook,
        ["minionnotebook"] = AgentId.MinionNotebook,
        ["orchestrion"] = AgentId.Orchestrion,
        ["adventurenotebook"] = AgentId.AdventureNotebook,
        ["aethercurrent"] = AgentId.AetherCurrent,
        ["contentsnote"] = AgentId.ContentsNote,
        ["pvpprofile"] = AgentId.PvpProfile,
        ["companions"] = AgentId.Buddy,
        ["chocobobreed"] = AgentId.ChocoboBreed,
        ["chocoborace"] = AgentId.ChocoboRace,
        ["goldsaucer"] = AgentId.GoldSaucer,
        ["tripletriad"] = AgentId.TripleTriad,
        ["currency"] = AgentId.Currency,
        ["retainer"] = AgentId.Retainer,
        ["freecompany"] = AgentId.FreeCompany,
        ["freecompanyprofile"] = AgentId.FreeCompanyProfile,
        ["freecompanychest"] = AgentId.FreeCompanyChest,
        ["gearsets"] = AgentId.GearSet,
        ["social"] = AgentId.Social,
        ["friendlist"] = AgentId.Friendlist,
        ["partysearch"] = AgentId.Search,
        ["lookingforparty"] = AgentId.LookingForGroup,
        ["linkshell"] = AgentId.Linkshell,
        ["blacklist"] = AgentId.Blacklist,
        ["contacts"] = AgentId.ContactList,
        ["teleport"] = AgentId.Teleport,
        ["recipenote"] = AgentId.RecipeNote,
        ["gatheringnote"] = AgentId.GatheringNote,
        ["materialize"] = AgentId.Materialize,
        ["materiaattach"] = AgentId.MateriaAttach,
        ["repair"] = AgentId.Repair,
        ["itemsearch"] = AgentId.ItemSearch,
        ["itemdetail"] = AgentId.ItemDetail,
        ["hudlayout"] = AgentId.HudLayout,
        ["config"] = AgentId.Config,
        ["characterconfig"] = AgentId.ConfigCharacter,
        ["chatconfig"] = AgentId.ChatConfig,
        ["keybindconfig"] = AgentId.Configkey,
        ["playguide"] = AgentId.PlayGuide,
        ["dutyfinder"] = AgentId.ContentsFinder,
        ["raidfinder"] = AgentId.RaidFinder,
        ["screenlog"] = AgentId.ScreenLog,
        ["howto"] = AgentId.HowTo,
        ["cabinet"] = AgentId.Cabinet,
        ["legacyitemstorage"] = AgentId.LegacyItemStorage,
        ["salvage"] = AgentId.Salvage,
        ["aquarium"] = AgentId.Aquarium,
        ["weatherreport"] = AgentId.WeatherReport,
        ["weblauncher"] = AgentId.WebLauncher,
        ["licenseviewer"] = AgentId.LicenseViewer,
        ["letter"] = AgentId.Letter,
        ["relicnotebook"] = AgentId.RelicNotebook,
        ["deepdungeonmap"] = AgentId.DeepDungeonMap,
        ["deepdungeonstatus"] = AgentId.DeepDungeonStatus,
    };

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        var names = OpenableAgents.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        registry.Add(
            "open_addon",
            "Open an addon (UI window)",
            "Opens one of the game's addon (UI window) surfaces the way its own keyboard shortcut " +
            "would: through the FFXIVClientStructs agent layer (AgentInterface.Show). Bounded to an " +
            "allowlist of standalone-openable system windows; anything that must be opened from a " +
            "parent context is refused by design. This is a mutating tool: it is hidden from " +
            "tools/list and refused by tools/call while the plugin's allow-mutating-tools switch " +
            "is off.",
            Json.SchemaWithEnum(
                "addon",
                names,
                "Which addon to open.",
                true),
            args => OpenAddon(args),
            mutating: true);

        registry.Add(
            "close_addon",
            "Close an addon (UI window)",
            "Closes one of the game's addon (UI window) surfaces through the agent layer " +
            "(AgentInterface.Hide), the same path the game's own close action takes. Accepts the " +
            "same allowlist names as open_addon. This is a mutating tool: it is hidden from " +
            "tools/list and refused by tools/call while the plugin's allow-mutating-tools switch " +
            "is off.",
            Json.SchemaWithEnum(
                "addon",
                names,
                "Which addon to close.",
                true),
            args => CloseAddon(args),
            mutating: true);

        registry.Add(
            "get_addon_state",
            "Get addon (UI window) state",
            "Reports which of the allowlisted addon surfaces the agent layer currently considers " +
            "active (its window open), keyed by the same names open_addon accepts. Read-only.",
            Json.Schema(),
            _ => AddonState());
    }

    // ---------------------------------------------------------------- handlers

    private static unsafe object OpenAddon(JObject args)
    {
        var addon = args["addon"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(addon))
            return Json.ToolError("missing required parameter: addon");

        if (!OpenableAgents.TryGetValue(addon, out var agentId))
            return Json.ToolError($"unknown addon '{addon}'; see open_addon's enum for the allowlist");

        return WithAgent(agentId, addon, agent =>
        {
            agent->Show();
            return new JObject
            {
                ["addon"] = addon,
                ["agentId"] = (int)agentId,
                ["opened"] = true,
            };
        });
    }

    private static unsafe object CloseAddon(JObject args)
    {
        var addon = args["addon"]?.Value<string>();
        if (string.IsNullOrWhiteSpace(addon))
            return Json.ToolError("missing required parameter: addon");

        if (!OpenableAgents.TryGetValue(addon, out var agentId))
            return Json.ToolError($"unknown addon '{addon}'; see close_addon's enum for the allowlist");

        return WithAgent(agentId, addon, agent =>
        {
            agent->Hide();
            return new JObject
            {
                ["addon"] = addon,
                ["agentId"] = (int)agentId,
                ["closed"] = true,
            };
        });
    }

    private static unsafe object AddonState()
    {
        var module = SafeAgentModule();
        if (module is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = "the agent module's static address has not resolved - the game may " +
                             "not be running, or the client has not finished initializing",
            };
        }

        var active = new JArray();
        var inactive = new JArray();
        foreach (var (name, agentId) in OpenableAgents.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var agent = SafeAgent(module, agentId);
            if (agent is null)
            {
                inactive.Add(name);
                continue;
            }

            var isActive = agent->IsAgentActive();
            (isActive ? active : inactive).Add(name);
        }

        return new JObject
        {
            ["available"] = true,
            ["active"] = active,
            ["inactive"] = inactive,
        };
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// Resolves the agent for <paramref name="agentId"/> and runs <paramref name="action"/> on
    /// it, converting every failure mode (unresolved module pointer, missing agent) into a
    /// structured answer rather than an escaped exception.
    /// </summary>
    private static unsafe object WithAgent(AgentId agentId, string addon, AgentAction action)
    {
        var module = SafeAgentModule();
        if (module is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = "the agent module's static address has not resolved - the game may " +
                             "not be running, or the client has not finished initializing",
            };
        }

        var agent = SafeAgent(module, agentId);
        if (agent is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = $"no agent is registered for id {(int)agentId} ('{addon}') on this client",
            };
        }

        return action(agent);
    }

    private unsafe delegate object AgentAction(AgentInterface* agent);

    private static unsafe AgentModule* SafeAgentModule()
    {
        try
        {
            return AgentModule.Instance();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static unsafe AgentInterface* SafeAgent(AgentModule* module, AgentId agentId)
    {
        try
        {
            return module->GetAgentByInternalId(agentId);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
