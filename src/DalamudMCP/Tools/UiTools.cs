using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace DalamudMCP.Tools;

/// <summary>
/// The plugin's first mutating tool set: opening and closing the game's addon (UI window)
/// surfaces through the FFXIVClientStructs agent layer.
///
/// <para>Every tool here is registered with <c>mutating: true</c>, so the whole set is invisible
/// to <c>tools/list</c> and refused by <c>tools/call</c> while the <c>AllowMutatingTools</c>
/// configuration switch is off 鈥?the same two-place gate every future state-changing tool
/// inherits. The actions themselves are bounded to what a player could do with a keyboard
/// shortcut: show or hide a system window, or ask which ones are open. The click surface
/// (click_addon_element) is likewise bounded to dispatching one Atk UI event at a synthetic
/// position inside a window the agent has already enumerated — the moral equivalent of moving
/// the mouse and clicking, not a macro engine: nothing here types text, fires addon callbacks
/// with crafted AtkValue payloads, or touches combat state.</para>
///
/// <para>The list/click mechanics go one layer below the agent layer, to the game's own UI
/// tree: RaptureAtkUnitManager resolves a loaded addon (AtkUnitBase), its RootNode tree is
/// walked for element discovery (ids, labels, screen rectangles), and clicking dispatches
/// AtkEventType.MouseClick / ButtonClick (or the component-list item path) through the
/// addon's own ReceiveEvent — the same dispatch the real input pipeline feeds. Every pointer
/// is null-checked rather than trusted, and every failure becomes a structured
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
        services = svc;
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

        registry.Add(
            "list_addon_elements",
            "List addon (UI window) elements",
            "Walks a loaded addon's UI node tree and reports its clickable elements: node id, " +
            "type, label text, screen rectangle, and visible/enabled state. Pass 'addon' as a " +
            "name from get_addon_state's allowlist, or 'addonId' as the raw AtkUnitBase id. " +
            "Use the returned nodeId with click_addon_element. Read-only.",
            Json.Schema(
                ("addon", "string", "Addon name to inspect (same names open_addon accepts)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to inspect (overrides addon)", false),
                ("maxDepth", "integer", "Maximum node-tree depth to walk (default 12)", false),
                ("maxNodes", "integer", "Maximum elements to report (default 100)", false),
                ("allNodes", "boolean", "Report every node, not just labeled/clickable ones (default false)", false)),
            args => ListAddonElements(args));

        registry.Add(
            "click_addon_element",
            "Click an addon (UI window) element",
            "Dispatches one UI event (default: left click) at a node inside a loaded addon, " +
            "through the addon's own event pipeline — the same dispatch a real mouse click " +
            "feeds it. Resolve a nodeId with list_addon_elements first. For list components " +
            "you may pass 'index' to click a list item directly. This is a mutating tool: it " +
            "is hidden from tools/list and refused by tools/call while the plugin's " +
            "allow-mutating-tools switch is off.",
            Json.Schema(
                ("addon", "string", "Addon name to click inside (same names open_addon accepts)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to click inside (overrides addon)", false),
                ("nodeId", "integer", "Target node id from list_addon_elements", false),
                ("index", "integer", "For list components: the item index to click instead of a nodeId", false),
                ("event", "string", "Which event to dispatch: click (default), doubleClick, buttonClick, buttonPress, buttonRelease, or 'registered' to fire every handler the node has registered (closest to a real mouse click)", false),
                ("param", "integer", "Override the event callback param (uint32). Advanced: the game normally derives it from the node's registered handler; ClickLib-style close buttons need 0xFFFFFFFF (-1)", false)),
            args => ClickAddonElement(args),
            mutating: true);

        // Temporary diagnostic tools for the click-dispatch investigation. Mutating: the
        // enable variant swaps a live vtable slot inside the game process.
        registry.Add(
            "probe_receive_event_enable",
            "Hook one addon's ReceiveEvent (diagnostic)",
            "Temporary diagnostic: swaps the ReceiveEvent vtable slot of one loaded addon so " +
            "every UI event it receives (real mouse clicks and synthetic dispatches alike) is " +
            "captured before being forwarded. Pass 'addon' (any loaded addon name) or 'addonId'. " +
            "Use probe_receive_event_dump to read captures. Mutating.",
            Json.Schema(
                ("addon", "string", "Addon name to hook (raw loaded addon name, e.g. Currency)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to hook (overrides addon)", false)),
            args => ProbeEnable(args),
            mutating: true);

        registry.Add(
            "probe_receive_event_disable",
            "Unhook the ReceiveEvent probe (diagnostic)",
            "Restores the ReceiveEvent vtable slot captured by probe_receive_event_enable. " +
            "Mutating.",
            Json.Schema(),
            _ => AtkEventProbe.Disable(),
            mutating: true);

        registry.Add(
            "probe_receive_event_enable_listener",
            "Hook one node's registered listener ReceiveEvent (diagnostic)",
            "Resolves the addon and node, then hooks the node's FIRST registered listener's " +
            "ReceiveEvent vtable slot. Real component clicks land on node-registered listeners " +
            "rather than the addon's own slot, so this captures what a real click delivers. " +
            "Pass 'addon' (raw loaded addon name) or 'addonId', plus 'nodeId'. Use " +
            "probe_receive_event_dump to read captures. Mutating.",
            Json.Schema(
                ("addon", "string", "Addon name to hook (raw loaded addon name, e.g. Currency)", false),
                ("addonId", "integer", "Raw AtkUnitBase id of the addon to hook (overrides addon)", false),
                ("nodeId", "integer", "Node whose first registered listener should be hooked", true)),
            args => ProbeEnableListener(args),
            mutating: true);

        registry.Add(
            "probe_receive_event_dump",
            "Dump captured ReceiveEvent calls (diagnostic)",
            "Returns every ReceiveEvent invocation captured since the hook was enabled: event " +
            "type, param, and the raw event / event-data buffers decoded at their FCS offsets. " +
            "Read-only.",
            Json.Schema(),
            _ => AtkEventProbe.Dump());

        // FireCallback probe (modelled on SimpleTweaks' Addon Logging → Callbacks): a
        // global inline detour recording every addon callback with its decoded AtkValue[]
        // arguments. This captures the semantic layer real UI clicks funnel into.
        registry.Add(
            "probe_callback_enable",
            "Hook the global FireCallback (diagnostic)",
            "Temporary diagnostic: installs a global hook on AtkUnitBase.FireCallback so every " +
            "addon callback the game performs is recorded with its decoded AtkValue[] " +
            "arguments — the same data SimpleTweaks' Addon Logging → Callbacks shows. Real UI " +
            "interactions (button clicks, list picks) funnel into these callbacks, so the " +
            "capture tells an agent exactly which (addon, values) payload reproduces an " +
            "action. Use probe_callback_dump to read captures. Mutating.",
            Json.Schema(),
            _ => CallbackProbe.Enable(),
            mutating: true);

        registry.Add(
            "probe_callback_disable",
            "Unhook the FireCallback probe (diagnostic)",
            "Removes the global FireCallback hook installed by probe_callback_enable. Mutating.",
            Json.Schema(),
            _ => CallbackProbe.Disable(),
            mutating: true);

        registry.Add(
            "probe_callback_dump",
            "Dump captured FireCallback calls (diagnostic)",
            "Returns every addon callback captured since probe_callback_enable: addon name, " +
            "value count, each AtkValue decoded by type (Int/UInt/Bool/String/...), the " +
            "close/update-visibility flag, and the return value. Read-only.",
            Json.Schema(),
            _ => CallbackProbe.Dump());
    }

    private static unsafe object ProbeEnable(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved for probe_receive_event_enable");
        return AtkEventProbe.Enable(unit);
    }

    /// <summary>Hook a specific node's first registered listener. Resolves the addon +
    /// node, walks the node's AtkEventManager event chain, and hooks the FIRST listener
    /// found there (that listener's own vtable slot). Real component clicks land here,
    /// not on the addon's vtable slot.</summary>
    private static unsafe object ProbeEnableListener(JObject args)
    {
        var unit = ResolveAddon(args["addon"]?.Value<string>(), args["addonId"]);
        if (unit is null)
            return Json.ToolError("no addon resolved for probe_receive_event_enable_listener");

        var nodeId = args["nodeId"]?.Value<int?>();
        if (nodeId is null)
            return Json.ToolError("missing required parameter: nodeId");

        var node = FindBestInUld(unit, (uint)nodeId.Value, out var bestScore);
        if (node is null || bestScore < 3)
        {
            var fromRoot = FindBestNode(unit->RootNode, (uint)nodeId.Value, out var rootScore);
            if (rootScore > bestScore)
            {
                node = fromRoot;
                bestScore = rootScore;
            }
        }
        if (node is null)
            return Json.ToolError($"no node with id {nodeId} was found in this addon");

        var em = &node->AtkEventManager;
        if (em->Event is null)
            return Json.ToolError($"node {nodeId} has no registered listeners");

        var listener = em->Event->Listener;
        if (listener is null)
            return Json.ToolError($"node {nodeId}'s first registered event has a null listener");

        return AtkEventProbe.EnableListener(unit, listener);
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
            // A freshly created addon instance ignores synthetic clicks until the game
            // has refreshed it once; a second Show() after a short delay restores click
            // responsiveness (live-verified alternating open/click pattern).
            ScheduleReshow(agentId, 1200);
            return new JObject
            {
                ["addon"] = addon,
                ["agentId"] = (int)agentId,
                ["opened"] = true,
            };
        });
    }

    /// <summary>Show() an agent a second time. Live-verified quirk: a freshly-created addon
    /// instance's event dispatch is not ready after the first Show() — synthetic clicks sent
    /// to it are silently ignored (reliably reproducible alternating open/click pattern on
    /// the Currency addon). A second Show() one-plus game-frame later restores click
    /// responsiveness, so OpenAddon schedules a delayed re-Show() fire-and-forget.</summary>
    private static GameServices? services;

    private static void ScheduleReshow(AgentId agentId, int delayMs)
    {
        var svc = services;
        if (svc is null)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(delayMs).ConfigureAwait(false);
                await svc.Framework.RunOnFrameworkThread(() => ReshowAgent(agentId)).ConfigureAwait(false);
            }
            catch
            {
                // fire-and-forget: a failed re-Show only means the click-readiness
                // quirk may resurface; never take the server down for it.
            }
        });
    }

    private static unsafe void ReshowAgent(AgentId agentId)
    {
        var mgr = SafeAgentModule();
        var a = mgr is not null ? SafeAgent(mgr, agentId) : null;
        if (a is not null)
            a->Show();
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

    // ---------------------------------------------------------------- addon element tools

    private static unsafe object ListAddonElements(JObject args)
    {
        var addonName = args["addon"]?.Value<string>();
        var addonIdArg = args["addonId"];
        var maxDepth = ClampInt(args["maxDepth"], 1, 40, 12);
        var maxNodes = ClampInt(args["maxNodes"], 1, 500, 100);
        var allNodes = args["allNodes"]?.Value<bool>() ?? false;

        var unit = ResolveAddon(addonName, addonIdArg);
        if (unit is null)
        {
            var loaded = string.Empty;
            var mgr = SafeRaptureAtkUnitManager();
            if (mgr is not null)
                loaded = DescribeLoadedAddons(mgr);
            return addonName is not null
                ? Json.ToolError($"no loaded addon named '{addonName}' was found; open it with " +
                                 $"open_addon first, or pass the addon's raw addonId.{loaded}")
                : Json.ToolError("no addon resolved; pass 'addon' (a name from the open_addon " +
                                 "allowlist) or 'addonId'");
        }

        var root = unit->RootNode;
        if (root is null)
        {
            return new JObject
            {
                ["available"] = false,
                ["reason"] = $"addon '{unit->NameString}' is loaded but has no root node yet",
            };
        }

        var elements = new JArray();
        var truncated = false;
        var visited = new HashSet<nuint>();
        // Top-level nodes live in the addon's UldManager node list (an array), not in the
        // RootNode's child chain — RootNode->ChildCount (e.g. 110) far exceeds the one
        // linked child, so the authoritative enumeration is UldManager.NodeList. The list
        // also contains nested nodes, so a visited set keeps the child/sibling descent from
        // emitting the same node twice.
        var uldNodeCount = Math.Min((int)unit->UldManager.NodeListCount, 512);
        for (var i = 0; i < uldNodeCount && !truncated; i++)
        {
            var n = unit->UldManager.NodeList[i];
            WalkNode(n, unit, 0, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);
        }

        if (elements.Count == 0)
            WalkNode(root, unit, 0, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["x"] = (int)unit->GetX(),
            ["y"] = (int)unit->GetY(),
            ["scale"] = (float)unit->Scale,
            ["truncated"] = truncated,
            ["elements"] = elements,
        };
    }

    private static unsafe object ClickAddonElement(JObject args)
    {
        var addonName = args["addon"]?.Value<string>();
        var addonIdArg = args["addonId"];
        var nodeIdArg = args["nodeId"];
        var indexArg = args["index"];
        var eventName = args["event"]?.Value<string>() ?? "click";
        var paramArg = args["param"];

        AtkEventType? eventType = eventName switch
        {
            null or "" or "click" => AtkEventType.MouseClick,
            "doubleClick" => AtkEventType.MouseDoubleClick,
            "buttonClick" => AtkEventType.ButtonClick,
            "buttonPress" => AtkEventType.ButtonPress,
            "buttonRelease" => AtkEventType.ButtonRelease,
            "registered" => AtkEventType.MouseClick, // event itself ignored; every registered handler fires
            _ => null,
        };
        if (eventType is null)
            return Json.ToolError($"unknown event '{eventName}'; use click, doubleClick, buttonClick, buttonPress or buttonRelease");

        var unit = ResolveAddon(addonName, addonIdArg);
        if (unit is null)
        {
            var loaded = string.Empty;
            var mgr = SafeRaptureAtkUnitManager();
            if (mgr is not null)
                loaded = DescribeLoadedAddons(mgr);
            return addonName is not null
                ? Json.ToolError($"no loaded addon named '{addonName}' was found; open it with " +
                                 $"open_addon first, or pass the addon's raw addonId.{loaded}")
                : Json.ToolError("no addon resolved; pass 'addon' (a name from the open_addon " +
                                 "allowlist) or 'addonId'");
        }

        // List-item shortcut: index into a list component dispatches the item event directly.
        if (indexArg is not null && nodeIdArg is null)
        {
            var index = indexArg.Value<int>();
            var list = FindListByIndex(unit, index);
            if (list is null)
                return Json.ToolError($"no visible list component with item index {index} was " +
                                      "found in this addon; list it with list_addon_elements");

            var listNodeId = list->OwnerNode is not null ? list->OwnerNode->NodeId : 0;
            list->SelectItem(index, true);

            return new JObject
            {
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["listNodeId"] = (int)listNodeId,
                ["index"] = index,
                ["clicked"] = true,
                ["via"] = "AtkComponentList.SelectItem",
            };
        }

        if (nodeIdArg is null)
            return Json.ToolError("nothing to click: pass 'nodeId' (from list_addon_elements) " +
                                  "or 'index' (for list components)");

        var nodeId = nodeIdArg.Value<uint>();
        // Search the addon's UldManager node list first — it contains every top-level and
        // nested node, whereas the RootNode child chain only exposes one linked child.
        // Duplicate ids are common (per-row components reuse ids 2-7), so among all matches
        // prefer a visible + clickable node; a bare Res/text match is the last resort.
        var node = FindBestInUld(unit, nodeId, out var bestScore);
        if (node is null || bestScore < 3)
        {
            var fromRoot = FindBestNode(unit->RootNode, nodeId, out var rootScore);
            if (rootScore > bestScore)
            {
                node = fromRoot;
                bestScore = rootScore;
            }
        }

        if (node is null)
            return Json.ToolError($"no node with id {nodeId} was found in this addon; list it " +
                                  "with list_addon_elements");
        if (!node->IsVisible())
            return Json.ToolError($"node {nodeId} is not currently visible");

        // ClickLib's proven dispatch shape (ClickBase.SendClick): ALWAYS dispatch through
        // the ADDON's own vtable ReceiveEvent (vfunc[2]) with the addon as both receiver
        // and evt.Listener — never the node's registered listener or the component. The
        // addon's override routes the event to the right handler internally. The event's
        // Target@0x8 is the clicked node; for component nodes ClickLib targets the
        // component's OwnerNode (the component node itself), and the callback Param is
        // what the game registered for that node (close buttons typically 0xFFFFFFFF with
        // EventType.Change).
        var sendType = eventType.Value;

        // Explicit param override wins (ClickLib hardcodes 0xFFFFFFFF for close buttons
        // where the auto-derived registered param may differ); otherwise read the param
        // the game registered for this node/event.
        uint eventParam;
        if (paramArg is not null)
        {
            eventParam = unchecked((uint)paramArg.Value<long>());
        }
        else
        {
            eventParam = node->GetEventParam(eventType.Value);

            // Read the node's registered event param if the generic probe returned 0 —
            // close buttons are registered with 0xFFFFFFFF which GetEventParam may not surface.
            for (var e = node->AtkEventManager.Event; e is not null; e = e->NextEvent)
            {
                if (eventParam == 0 && e->Param != 0)
                    eventParam = e->Param;
            }
            if (eventParam == 0)
                eventParam = 0xFFFFFFFF;
        }

        // For component nodes prefer the component's OwnerNode as the event target and the
        // param registered on the component node (button callback ids live there).
        var targetNode = (AtkResNode*)node;
        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (compNode is not null && compNode->Component is not null &&
                compNode->Component->OwnerNode is not null)
            {
                targetNode = (AtkResNode*)compNode->Component->OwnerNode;
            }
        }

        var screenX = (short)(node->ScreenX + (node->Width / 2f));
        var screenY = (short)(node->ScreenY + (node->Height / 2f));

        // ClickLib passes a FULLY ZEROED arg5 — synthetic clicks carry no mouse data.

        // ClickLib's SendClick, byte-for-byte: a zeroed 0x40 buffer with the target node
        // at +0x8 and the addon at +0x10 — NOT a full AtkEvent (Param and State stay
        // zero; the callback id travels only in the a3 argument). Buffer sizes larger
        // than the managed AtkEvent struct matter for handlers that read past it.
        byte* evtBuf = stackalloc byte[0x40];
        for (var i = 0; i < 0x40; i++)
            evtBuf[i] = 0;
        // CN client 7.x: the addon's ReceiveEvent dereferences evt->Node (a synthetic event
        // with a null Node crashed the game in live testing), so fill Node@0x0 as well —
        // not just Target@0x8 / Listener@0x10 as ClickLib's older intl-client layout did.
        *(AtkResNode**)&evtBuf[0x0] = targetNode;
        *(AtkEventTarget**)&evtBuf[0x8] = (AtkEventTarget*)targetNode;
        *(AtkEventListener**)&evtBuf[0x10] = (AtkEventListener*)unit;

        // ClickLib passes a FULLY ZEROED 0x40-byte arg5 — synthetic clicks carry no mouse
        // data. AtkEventData is smaller than 0x40, so a handler that reads past the managed
        // struct would touch stack garbage; zero the full native-size buffer instead.
        byte* eventDataBuf = stackalloc byte[0x40];
        for (var i = 0; i < 0x40; i++)
            eventDataBuf[i] = 0;

        if (eventName == "registered")
        {
            // Genuine-mouse-click replication: fire every (type, param, listener) triple the
            // node actually has registered — a real click routes through the node's event
            // manager to ITS listeners (component buttons, lists, ...), which then fire the
            // addon-level callback. Dispatching to the addon vtable only covers addons whose
            // ReceiveEvent override switches on addon-level (type, param) pairs.
            var fired = 0;
            Span<byte> regSpan = stackalloc byte[0x40];
            for (var e = node->AtkEventManager.Event; e is not null; e = e->NextEvent)
            {
                if (e->Listener is null)
                    continue;
                regSpan.Clear();
                fixed (byte* regBuf = regSpan)
                {
                    *(AtkResNode**)&regBuf[0x0] = targetNode;
                    *(AtkEventTarget**)&regBuf[0x8] = (AtkEventTarget*)targetNode;
                    *(AtkEventListener**)&regBuf[0x10] = e->Listener;
                    var listenerVtbl = (AtkEventListener.AtkEventListenerVirtualTable*)e->Listener->VirtualTable;
                    listenerVtbl->ReceiveEvent(e->Listener, e->State.EventType, (int)e->Param, (AtkEvent*)regBuf, (AtkEventData*)eventDataBuf);
                    fired++;
                }
            }

            return new JObject
            {
                ["addon"] = unit->NameString,
                ["addonId"] = (int)unit->Id,
                ["nodeId"] = (int)nodeId,
                ["nodeType"] = node->Type.ToString(),
                ["event"] = "registered",
                ["fired"] = fired,
                ["screenX"] = (int)screenX,
                ["screenY"] = (int)screenY,
                ["clicked"] = fired > 0,
                ["via"] = "registered listeners",
            };
        }

        // Dispatch through the ADDON's vtable ReceiveEvent slot (offset 16, slot 2) —
        // ClickLib's exact path. The addon's virtual override performs its own handler
        // routing; dispatching to nested listeners bypasses it and drops the event.
        var vtbl = (AtkEventListener.AtkEventListenerVirtualTable*)unit->VirtualTable;
        vtbl->ReceiveEvent((AtkEventListener*)unit, sendType, (int)eventParam, (AtkEvent*)evtBuf, (AtkEventData*)eventDataBuf);

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = (int)unit->Id,
            ["nodeId"] = (int)nodeId,
            ["nodeType"] = node->Type.ToString(),
            ["event"] = sendType.ToString(),
            ["eventParam"] = (int)eventParam,
            ["screenX"] = (int)screenX,
            ["screenY"] = (int)screenY,
            ["clicked"] = true,
            ["via"] = "addon vtable",
        };
    }

    // ---------------------------------------------------------------- node helpers

    /// <summary>
    /// Resolves an AtkUnitBase either by allowlist name (through the agent layer's addon
    /// names) or by raw addon id, via RaptureAtkUnitManager.
    /// </summary>
    private static unsafe AtkUnitBase* ResolveAddon(string? addonName, JToken? addonIdArg)
    {
        var manager = SafeRaptureAtkUnitManager();
        if (manager is null)
            return null;

        if (addonIdArg is not null)
        {
            var id = addonIdArg.Value<int>();
            if (id < 0 || id > ushort.MaxValue)
                return null;
            return manager->GetAddonById((ushort)id);
        }

        if (string.IsNullOrWhiteSpace(addonName))
            return null;

        // The client registers addons under their internal names ("CurrencyList",
        // "_AddonExample", ...), which rarely equal the agent-allowlist keys, so resolve by
        // scanning the loaded list for a case-insensitive name match. Direct GetAddonByName
        // is tried first because it is the game's own lookup.
        var byName = manager->GetAddonByName(addonName, 1);
        if (byName is not null)
            return byName;

        var list = manager->AllLoadedUnitsList;
        var entries = list.Entries;
        for (var i = 0; i < entries.Length; i++)
        {
            var p = entries[i];
            if (p.IsNull)
                continue;
            var unit = p.Value;
            if (unit is null)
                continue;
            var name = unit->NameString;
            if (string.Equals(name, addonName, StringComparison.OrdinalIgnoreCase) ||
                name.Contains(addonName, StringComparison.OrdinalIgnoreCase))
                return unit;
        }

        return null;
    }

    /// <summary>Best-effort snapshot of the loaded addon names for an error message.</summary>
    private static unsafe string DescribeLoadedAddons(RaptureAtkUnitManager* manager)
    {
        var names = new List<string>();
        TryCollectAddonNames(manager->AllLoadedUnitsList, names);
        if (names.Count == 0)
            TryCollectAddonNames(manager->FocusedUnitsList, names);

        return names.Count > 0 ? $" Loaded addons include: {string.Join(", ", names)}." : " (No loaded addons were enumerable.)";
    }

    private static unsafe void TryCollectAddonNames(AtkUnitList list, List<string> names)
    {
        try
        {
            var entries = list.Entries;
            foreach (var p in entries)
            {
                if (p.IsNull)
                    continue;
                try
                {
                    var unit = p.Value;
                    if (unit is null)
                        continue;
                    var name = unit->NameString;
                    if (!string.IsNullOrEmpty(name) && !names.Contains(name))
                        names.Add(name);
                    if (names.Count >= 30)
                        return;
                }
                catch (Exception)
                {
                    // unreadable entry — skip it, keep collecting
                }
            }
        }
        catch (Exception)
        {
            // unreadable list — nothing to add
        }
    }

    /// <summary>Depth-first walk collecting clickable/labeled elements into <paramref name="elements"/>.</summary>
    private static unsafe void WalkNode(
        AtkResNode* node,
        AtkUnitBase* unit,
        int depth,
        int maxDepth,
        int maxNodes,
        bool allNodes,
        JArray elements,
        ref bool truncated,
        HashSet<nuint> visited)
    {
        if (node is null || truncated || depth > maxDepth)
            return;
        if (!visited.Add((nuint)node))
            return;

        var label = NodeLabel(node);
        var clickable = NodeClickable(node);
        if (allNodes || label is not null || clickable)
        {
            if (elements.Count >= maxNodes)
            {
                truncated = true;
                return;
            }

            elements.Add(NodeJson(node, unit, label, clickable, depth));
        }

        WalkNode(node->ChildNode, unit, depth + 1, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);

        // A component node's contents live on the component's UldManager node list (its
        // AtkResNode* back-reference chains the component's own tree), not among the res-node
        // children — descend so buttons/lists inside components are listed.
        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (compNode is not null && compNode->Component is not null)
            {
                // The component's widget tree lives in its own UldManager node list; the
                // AtkResNode back-reference is frequently null on live clients.
                var compUld = compNode->Component->UldManager;
                var compCount = Math.Min((int)compUld.NodeListCount, 512);
                if (compCount > 0 && compUld.NodeList is not null)
                {
                    for (var i = 0; i < compCount && !truncated; i++)
                        WalkNode(compUld.NodeList[i], unit, depth + 1, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);
                }
                else if (compNode->Component->AtkResNode is not null)
                {
                    WalkNode(compNode->Component->AtkResNode, unit, depth + 1, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);
                }
            }
        }

        WalkNode(node->NextSiblingNode, unit, depth, maxDepth, maxNodes, allNodes, elements, ref truncated, visited);
    }


    private static unsafe JObject NodeJson(AtkResNode* node, AtkUnitBase* unit, string? label, bool clickable, int depth)
    {
        return new JObject
        {
            ["nodeId"] = (int)node->NodeId,
            ["type"] = node->Type.ToString(),
            ["depth"] = depth,
            ["label"] = label,
            ["x"] = (int)node->ScreenX,
            ["y"] = (int)node->ScreenY,
            ["width"] = (int)node->Width,
            ["height"] = (int)node->Height,
            ["visible"] = node->IsVisible(),
            ["enabled"] = (node->NodeFlags & NodeFlags.Enabled) != 0,
            ["clickable"] = clickable,
        };
    }

    /// <summary>
    /// Component nodes report combined runtime type values (observed 1004 on live clients)
    /// rather than the bare 10000 enum constant, so component detection uses a >=1000 test.
    /// </summary>
    private static unsafe bool IsComponentType(AtkResNode* node) => (int)node->Type >= 1000;

    /// <summary>
    /// Casts a component node directly instead of via <c>GetAsAtkComponentNode</c>:
    /// live clients report combined type values (e.g. 1004) that the helper's strict
    /// enum check rejects, while the memory layout (AtkResNode at offset 0) is identical.
    /// </summary>
    private static unsafe AtkComponentNode* ComponentNodeOf(AtkResNode* node) =>
        IsComponentType(node) ? (AtkComponentNode*)node : null;

    /// <summary>Label text for text/button/list nodes; null when the node carries no text.</summary>
    private static unsafe string? NodeLabel(AtkResNode* node)
    {
        try
        {
            if (node->Type == NodeType.Text)
            {
                var text = node->GetAsAtkTextNode();
                var cp = text->GetText();
                return cp.HasValue ? cp.ToString() : null;
            }

            if (IsComponentType(node))
            {
                var compNode = ComponentNodeOf(node);
                var comp = compNode->Component;
                if (comp is null)
                    return null;

                // Button: its own text node.
                var button = compNode->GetAsAtkComponentButton();
                if (button is not null && button->ButtonTextNode is not null)
                {
                    var cp = button->ButtonTextNode->GetText();
                    var s = cp.HasValue ? cp.ToString() : null;
                    if (!string.IsNullOrWhiteSpace(s))
                        return s;
                }

                // List: first item labels, so the agent can see what's selectable.
                var list = compNode->GetAsAtkComponentList();
                if (list is not null)
                {
                    var items = new JArray();
                    var count = Math.Min(list->GetItemCount(), 8);
                    for (var i = 0; i < count; i++)
                    {
                        var cp = list->GetItemLabel(i);
                        if (cp.HasValue)
                            items.Add(cp.ToString());
                    }

                    return items.Count > 0 ? items.ToString(Newtonsoft.Json.Formatting.None) : null;
                }

                // Generic component: try the component's own text nodes by id 2.
                var renderer = compNode->GetAsAtkComponentListItemRenderer();
                if (renderer is not null)
                {
                    var tn = renderer->GetTextNodeById(2);
                    var cp = tn is not null ? tn->GetText() : default;
                    return cp is { HasValue: true } ? cp.ToString() : null;
                }
            }
        }
        catch (Exception)
        {
            return null;
        }

        return null;
    }

    /// <summary>Whether a node can plausibly receive click events.</summary>
    private static unsafe bool NodeClickable(AtkResNode* node)
    {
        if (node->Type == NodeType.Collision)
            return (node->NodeFlags & NodeFlags.RespondToMouse) != 0
                   || node->IsEventRegistered(AtkEventType.MouseClick);

        if (IsComponentType(node))
        {
            var compNode = ComponentNodeOf(node);
            if (compNode is null || compNode->Component is null)
                return false;

            var kind = compNode->GetAsAtkComponentButton() is not null
                       || compNode->GetAsAtkComponentCheckBox() is not null
                       || compNode->GetAsAtkComponentList() is not null
                       || compNode->GetAsAtkComponentTreeList() is not null
                       || compNode->GetAsAtkComponentIconText() is not null
                       || compNode->GetAsAtkComponentSlider() is not null
                       || compNode->GetAsAtkComponentTextInput() is not null;
            return kind || node->IsEventRegistered(AtkEventType.MouseClick);
        }

        return false;
    }

    /// <summary>Finds the first node with the given id in the addon's node tree.</summary>
    private static unsafe AtkResNode* FindNode(AtkResNode* node, uint nodeId)
    {
        while (node is not null)
        {
            if (node->NodeId == nodeId)
                return node;

            var inChild = FindNode(node->ChildNode, nodeId);
            if (inChild is not null)
                return inChild;

            node = node->NextSiblingNode;
        }

        return null;
    }

    /// <summary>
    /// Scores every node with <paramref name="nodeId"/> across the addon's UldManager node
    /// list (visible +2, clickable +1) and returns the best match.
    /// </summary>
    private static unsafe AtkResNode* FindBestInUld(AtkUnitBase* unit, uint nodeId, out int bestScore)
    {
        AtkResNode* best = null;
        bestScore = -1;
        var uldCount = Math.Min((int)unit->UldManager.NodeListCount, 512);
        for (var i = 0; i < uldCount; i++)
        {
            var candidate = FindBestNode(unit->UldManager.NodeList[i], nodeId, out var score);
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }

            if (bestScore == 3)
                break;
        }

        return best;
    }

    /// <summary>
    /// Walks the tree rooted at <paramref name="root"/>, scoring every node with the given
    /// id (visible +2, clickable +1) and keeping the best match. Duplicate ids are common
    /// (per-row components reuse ids 2-7), so the highest-scoring match wins.
    /// </summary>
    private static unsafe AtkResNode* FindBestNode(AtkResNode* root, uint nodeId, out int bestScore)
    {
        AtkResNode* best = null;
        var bestLocal = -1;

        Visit(root);
        bestScore = bestLocal;
        return best;

        // Local function (not lambda): lambdas capturing pointer locals are fine, but a
        // recursive lambda needs an explicit delegate type; a local function needs none.
        // bestScore is copied to bestLocal first: out params cannot be captured.
        void Visit(AtkResNode* node)
        {
            while (node is not null)
            {
                if (node->NodeId == nodeId)
                {
                    var score = 0;
                    if (node->IsVisible()) score += 2;
                    if (NodeClickable(node)) score += 1;
                    if (score > bestLocal)
                    {
                        bestLocal = score;
                        best = node;
                    }
                }

                Visit(node->ChildNode);
                node = node->NextSiblingNode;
            }
        }
    }

    /// <summary>
    /// Finds a visible list component whose item count exceeds <paramref name="index"/>,
    /// so 'index' clicks land on the right list without the caller naming it.
    /// </summary>
    private static unsafe AtkComponentList* FindListByIndex(AtkUnitBase* unit, int index)
    {
        return FindListNode(unit->RootNode, index);

        static unsafe AtkComponentList* FindListNode(AtkResNode* node, int index)
        {
            while (node is not null)
            {
                if (IsComponentType(node) && node->IsVisible())
                {
                    var compNode = ComponentNodeOf(node);
                    var list = compNode->Component is null ? null : compNode->GetAsAtkComponentList();
                    if (list is not null && index < list->GetItemCount())
                        return list;
                }

                var inChild = FindListNode(node->ChildNode, index);
                if (inChild is not null)
                    return inChild;

                node = node->NextSiblingNode;
            }

            return null;
        }
    }

    private static int ClampInt(JToken? token, int min, int max, int fallback)
    {
        if (token is null)
            return fallback;
        try
        {
            var v = token.Value<int>();
            return Math.Clamp(v, min, max);
        }
        catch (Exception)
        {
            return fallback;
        }
    }

    private static unsafe RaptureAtkUnitManager* SafeRaptureAtkUnitManager()
    {
        try
        {
            return RaptureAtkUnitManager.Instance();
        }
        catch (Exception)
        {
            return null;
        }
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
