using System;
using System.Linq;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using LuminaAction = Lumina.Excel.Sheets.Action;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Tools that act on the game: casting actions, setting targets, interacting, and
/// movement toggles. All of them are registered as mutating, so none are visible
/// unless the mutating-tool gate is enabled in the settings.
/// </summary>
internal static unsafe class ControlTools
{
    private const ulong SelfTargetId = 0xE0000000;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "execute_action",
            "Execute action",
            "Casts a job/general action by id, optionally by action name resolved from the Action sheet. " +
            "Uses the normal UseAction path, so cooldowns, cast times and range checks all apply. " +
            "Pass dryRun to ask whether the action could be used right now without using it.",
            Json.Schema(
                ("actionId", "integer", "Action row id", false),
                ("actionName", "string", "Exact action sheet name; resolved to an id", false),
                ("targetObjectId", "integer", "Target game object id (defaults to self)", false),
                ("dryRun", "boolean", "Check usability instead of using the action", false),
                ("requiredClassJobId", "integer", "Optional guard: refuse unless the current job has this id", false),
                ("requiredTerritoryId", "integer", "Optional guard: refuse unless the current territory has this id", false)),
            args => ExecuteAction(svc, args),
            mutating: true);

        registry.Add(
            "use_duty_action",
            "Use duty action",
            "Fires one of the two duty actions (the extra buttons a duty grants), addressed as slot 1 or 2.",
            Json.Schema(
                ("slot", "integer", "Duty action slot: 1 or 2", true)),
            args => UseDutyAction(svc, args),
            mutating: true);

        registry.Add(
            "use_general_action",
            "Use general action",
            "Executes a general action by id (e.g. 2 jump, 4 sprint, 13 auto-run toggle, 23 dismount, " +
            "44 accept raise).",
            Json.Schema(
                ("actionId", "integer", "General action id", true)),
            args => UseGeneralAction(svc, args),
            mutating: true);

        registry.Add(
            "set_target",
            "Set target",
            "Targets a game object by id or by name (exact match first, then substring).",
            Json.Schema(
                ("targetObjectId", "integer", "Game object id to target", false),
                ("targetName", "string", "Object name to target", false)),
            args => SetTarget(svc, args, focus: false),
            mutating: true);

        registry.Add(
            "set_focus_target",
            "Set focus target",
            "Sets the focus target by id or name; with no arguments the focus target is cleared.",
            Json.Schema(
                ("targetObjectId", "integer", "Game object id to focus", false),
                ("targetName", "string", "Object name to focus", false)),
            args => SetTarget(svc, args, focus: true),
            mutating: true);

        registry.Add(
            "interact_with_target",
            "Interact with target",
            "Interacts with the current target (talk to NPCs, open chests, gather, etc.).",
            Json.Schema(),
            _ => InteractWithTarget(svc),
            mutating: true);

        registry.Add(
            "dismount",
            "Dismount",
            "Dismounts the current mount (no-op report when not mounted).",
            Json.Schema(),
            _ => General(svc, 23, reportMounted: true),
            mutating: true);

        registry.Add(
            "cancel_cast",
            "Cancel cast",
            "Cancels the action currently being cast.",
            Json.Schema(),
            _ => CancelCast(svc),
            mutating: true);

        registry.Add(
            "accept_raise",
            "Accept raise",
            "Accepts a pending raise/rescue.",
            Json.Schema(),
            _ => General(svc, 44),
            mutating: true);

        registry.Add(
            "toggle_sprint",
            "Toggle sprint",
            "Toggles the sprint general action.",
            Json.Schema(),
            _ => General(svc, 4),
            mutating: true);

        registry.Add(
            "jump",
            "Jump",
            "Presses the jump general action once.",
            Json.Schema(),
            _ => General(svc, 2),
            mutating: true);

        registry.Add(
            "toggle_autorun",
            "Toggle auto-run",
            "Toggles auto-run on or off.",
            Json.Schema(),
            _ => General(svc, 13),
            mutating: true);

        registry.Add(
            "face_target",
            "Face target",
            "Turns the character to face the current target.",
            Json.Schema(),
            _ => FaceTarget(svc),
            mutating: true);
    }

    // ------------------------------------------------------------------
    // Handlers
    // ------------------------------------------------------------------

    private static JObject ExecuteAction(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        uint actionId = 0;
        if (args.TryGetValue("actionId", out var idTok))
            actionId = (uint)idTok.Value<long>();

        if (actionId == 0 && args.TryGetValue("actionName", out var nameTok))
        {
            var name = nameTok.Value<string>()?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new ToolException("actionName must not be empty");

            var row = svc.Sheet<LuminaAction>().FirstOrDefault(r =>
                r.Name.ExtractText().Equals(name, StringComparison.OrdinalIgnoreCase));
            if (row.RowId == 0)
                throw new ToolException($"no action named '{name}'");
            actionId = row.RowId;
        }

        if (actionId == 0)
            throw new ToolException("provide actionId or actionName");

        var targetId = SelfTargetId;
        if (args.TryGetValue("targetObjectId", out var targetTok))
            targetId = (ulong)targetTok.Value<long>();

        // Optional guards let a script assert the character is in the state the action expects
        // before anything is cast, instead of firing into the wrong job or zone.
        if (args.TryGetValue("requiredClassJobId", out var jobTok))
        {
            var required = (uint)jobTok.Value<long>();
            var current = svc.PlayerState.ClassJob.RowId;
            if (current != required)
                throw new ToolException($"current job is {current}, but this action requires job {required}");
        }

        if (args.TryGetValue("requiredTerritoryId", out var territoryTok))
        {
            var required = (uint)territoryTok.Value<long>();
            var current = svc.ClientState.TerritoryType;
            if (current != required)
                throw new ToolException($"current territory is {current}, but this action requires territory {required}");
        }

        var manager = RequireActionManager();

        if (args.Value<bool?>("dryRun") == true)
        {
            var status = manager->GetActionStatus(ActionType.Action, actionId, targetId, true, true, null);
            return new JObject
            {
                ["actionId"] = actionId,
                ["targetObjectId"] = targetId,
                ["dryRun"] = true,
                ["status"] = status,
                ["usable"] = status == 0,
                ["note"] = status == 0
                    ? "the action could be used right now"
                    : "the game reports the action is not currently available (cooldown, range, resource, or target)",
            };
        }

        var ok = manager->UseAction(ActionType.Action, actionId, targetId, 0, 0, 0, null);
        return new JObject
        {
            ["actionId"] = actionId,
            ["targetObjectId"] = targetId,
            ["success"] = ok,
        };
    }

    private static JObject UseDutyAction(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var slot = args.Value<int?>("slot") ?? 0;
        if (slot is < 1 or > 2)
            throw new ToolException("slot must be 1 or 2");

        var uiModule = FFXIVClientStructs.FFXIV.Client.UI.UIModule.Instance();
        if (uiModule is null)
            throw new ToolException("UIModule is not available");

        var hotbar = uiModule->GetRaptureHotbarModule();
        if (hotbar is null)
            throw new ToolException("the hotbar module is not available");

        if (!hotbar->DutyActionsPresent)
            throw new ToolException("this duty grants no duty actions, or they are not active right now");

        var index = (uint)(slot - 1);
        var used = hotbar->ExecuteDutyActionSlot(index);
        return new JObject
        {
            ["success"] = used,
            ["slot"] = slot,
        };
    }

    private static JObject UseGeneralAction(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);
        var actionId = (uint)(args.Value<int?>("actionId") ?? 0);
        if (actionId == 0)
            throw new ToolException("actionId is required");

        var manager = RequireActionManager();
        var ok = manager->UseAction(ActionType.GeneralAction, actionId, SelfTargetId, 0, 0, 0, null);
        return new JObject
        {
            ["actionId"] = actionId,
            ["actionType"] = "GeneralAction",
            ["success"] = ok,
        };
    }

    private static JObject SetTarget(GameServices svc, JObject args, bool focus)
    {
        EnsureLoggedIn(svc);

        IGameObject? target = null;
        if (args.TryGetValue("targetObjectId", out var idTok))
            target = svc.ObjectTable.SearchById((ulong)idTok.Value<long>());

        if (target is null && args.TryGetValue("targetName", out var nameTok))
        {
            var name = nameTok.Value<string>()?.Trim() ?? string.Empty;
            if (name.Length == 0)
                throw new ToolException("targetName must not be empty");

            target = svc.ObjectTable.FirstOrDefault(o =>
                o is not null && string.Equals(Conv.Str(o.Name), name, StringComparison.OrdinalIgnoreCase))
                ?? svc.ObjectTable.FirstOrDefault(o =>
                    o is not null && Conv.Str(o.Name).Contains(name, StringComparison.OrdinalIgnoreCase));
        }

        if (focus && target is null)
        {
            svc.TargetManager.FocusTarget = null;
            return new JObject { ["success"] = true, ["cleared"] = true };
        }

        if (target is null)
            throw new ToolException("target not found");

        if (focus) svc.TargetManager.FocusTarget = target;
        else svc.TargetManager.Target = target;

        return new JObject
        {
            ["success"] = true,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
            ["gameObjectId"] = target.GameObjectId,
        };
    }

    private static JObject InteractWithTarget(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var target = svc.TargetManager.Target
            ?? throw new ToolException("no current target");

        var system = TargetSystem.Instance();
        if (system is null)
            throw new ToolException("TargetSystem is not available");
        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)target.Address;
        if (native is null)
            throw new ToolException("target object address is not available");

        var result = system->InteractWithObject(native, false);
        return new JObject
        {
            ["success"] = result != 0,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
        };
    }

    private static JObject General(GameServices svc, uint generalActionId, bool reportMounted = false)
    {
        EnsureLoggedIn(svc);

        if (reportMounted && !svc.Condition[ConditionFlag.Mounted])
            return new JObject { ["note"] = "not mounted" };

        var manager = RequireActionManager();
        var ok = manager->UseAction(ActionType.GeneralAction, generalActionId, SelfTargetId, 0, 0, 0, null);
        return new JObject { ["success"] = ok };
    }

    private static JObject CancelCast(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var uiState = FFXIVClientStructs.FFXIV.Client.Game.UI.UIState.Instance();
        if (uiState is null)
            throw new ToolException("UIState is not available");
        uiState->Hotbar.CancelCast();
        return new JObject { ["success"] = true };
    }

    private static JObject FaceTarget(GameServices svc)
    {
        EnsureLoggedIn(svc);
        var target = svc.TargetManager.Target
            ?? throw new ToolException("no current target");

        var manager = RequireActionManager();
        var position = target.Position;
        manager->AutoFaceTargetPosition(&position, 0);
        return new JObject
        {
            ["success"] = true,
            ["name"] = Conv.NullIfEmpty(Conv.Str(target.Name)),
        };
    }

    private static void EnsureLoggedIn(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");
    }

    private static ActionManager* RequireActionManager()
    {
        var manager = ActionManager.Instance();
        return manager is not null
            ? manager
            : throw new ToolException("ActionManager is not available");
    }
}
