using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using InteropGenerator.Runtime;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Semantic addon callbacks and duty finder agent control.
///
/// <para>
/// Unlike <see cref="UiTools"/>, which emulates the mouse/keyboard level a shortcut would produce,
/// this set calls the game's own higher level entry points directly: the <c>AtkUnitBase.FireCallback</c>
/// virtual with a caller supplied <c>AtkValue</c> payload, and the <c>AgentContentsFinder</c> methods
/// the native UI itself uses when a player clicks through the duty finder.
/// </para>
/// <para>
/// Both are reached through FFXIVClientStructs member function pointers, so nothing here needs a
/// plugin specific assembly, a signature scan of its own, or a screen coordinate. Everything is
/// opt in behind the mutating tools switch.
/// </para>
/// </summary>
internal static class CallbackTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "fire_addon_callback",
            "Fire an addon callback with a crafted payload",
            "Fires the addon's own callback virtual (AtkUnitBase.FireCallback) with an explicit AtkValue payload, the same call the native UI makes when a player activates a widget. Use this only when no click path exists: unlike click_addon_element it can express semantic callbacks such as \"unselect\" or \"join\" that the addon dispatches on the callback number rather than on the clicked node.",
            Json.Schema(
                ("addon", "string", "Name of the loaded addon, for example ContentsFinder.", true),
                ("addonId", "integer", "Raw addon id, used instead of addon when the name is ambiguous.", false),
                ("values", "array of integer", "Callback payload. Almost every addon treats values[0] as the callback number and the rest as parameters. Values are written as 32 bit integers unless types says otherwise. Defaults to an empty payload.", false),
                ("types", "array of string", "Optional per value type override, same length as values. Each entry is one of int, uint, bool, float, string. Defaults to int for every value.", false),
                ("strings", "array of string", "Optional per value string override, same length as values. When a value's type is string the matching entry here is used as its contents. Defaults to the empty string.", false),
                ("close", "boolean", "Pass true to let the addon close itself after handling the callback. Defaults to false.", false)),
            args => FireAddonCallback(args),
            mutating: true);

        registry.Add(
            "get_contents_finder_state",
            "Read duty finder state",
            "Reads the duty finder agent, the queue state and the game's current content finder condition id. Useful for checking whether a duty selection or a queue actually took effect before acting on it.",
            Json.Schema(),
            _ => ContentsFinderState());

        registry.Add(
            "open_duty_finder_duty",
            "Open a duty in the duty finder",
            "Selects a duty with the duty finder agent (AgentContentsFinder.OpenRegularDuty) exactly as the native list does when a player picks an entry, optionally toggling unrestricted party first. This is the reliable way to reach a duty without depending on list row coordinates. The selection is asynchronous; poll get_contents_finder_state until the selection or the content finder condition id updates.",
            Json.Schema(
                ("cfc", "integer", "ContentFinderCondition row id of the duty to select, for example 290.", true),
                ("unrestricted", "boolean", "Set the unrestricted party (undersized) flag before selecting. Omit to leave it untouched.", false),
                ("hideIfShown", "boolean", "Pass true to hide the duty finder window if it is already open. Defaults to false.", false)),
            args => OpenDutyFinderDuty(args),
            mutating: true);

        registry.Add(
            "get_duty_list",
            "Read the duty finder duty list",
            "Walks the duty finder's duty list component (AddonContentsFinder.DutyList) and reports every entry the native list is holding: the category headers, their nested duties, the selected row index and the callback that would toggle the selected group open. Use this to drive the list by index instead of by screen coordinate.",
            Json.Schema(
                ("addon", "string", "Name of the loaded duty finder addon. Defaults to ContentsFinder.", false),
                ("addonId", "integer", "Raw addon id of the duty finder window, used when the name is ambiguous.", false),
                ("maxItems", "integer", "Maximum number of list entries to return. Defaults to 200, capped at 2000.", false),
                ("includeNames", "boolean", "Read each entry's own row text. Defaults to true; set false for a cheap index and group overview.", false)),
            args => DutyList(args));

        registry.Add(
            "select_duty_list_group",
            "Toggle the selected duty list group",
            "Replicates AutoDuty's SelectDuty: while the duty finder's selected row is empty, it fires the list's callback 3 with the number of section headers above the selected row plus one, which expands that group so its duties become selectable. Use it when open_duty_finder_duty has already selected a duty but the join callback is still refused.",
            Json.Schema(
                ("addon", "string", "Name of the loaded duty finder addon. Defaults to ContentsFinder.", false),
                ("addonId", "integer", "Raw addon id of the duty finder window, used when the name is ambiguous.", false),
                ("hideIfShown", "boolean", "Open the duty finder first with the given duty if the window is not loaded. Omit to require an already loaded window.", false),
                ("cfc", "integer", "Duty to select through the agent before toggling a group, used together with hideIfShown.", false)),
            args => SelectDutyListGroup(args),
            mutating: true);
    }

    private static unsafe object FireAddonCallback(JObject args)
    {
        var addonName = args["addon"]?.Value<string>();
        var unit = UiTools.ResolveAddon(addonName, args["addonId"]);
        if (unit is null)
        {
            return Json.ToolError(
                $"no loaded addon named '{addonName}' was found; open it with open_addon first, or pass the addon's raw addonId.");
        }

        var name = unit->NameString;
        if (!unit->IsReady)
        {
            return Json.ToolError($"addon '{name}' is not ready yet");
        }

        var values = ReadIntArray(args["values"]);
        var types = ReadStringArray(args["types"]);
        var strings = ReadStringArray(args["strings"]);
        var close = args["close"]?.Value<bool>() ?? false;

        if (values.Length > 32)
        {
            return Json.ToolError($"at most 32 callback values are supported; got {values.Length}");
        }

        if (types.Length > 0 && types.Length != values.Length)
        {
            return Json.ToolError($"types has {types.Length} entries but values has {values.Length}; they must match");
        }

        if (strings.Length > 0 && strings.Length != values.Length)
        {
            return Json.ToolError($"strings has {strings.Length} entries but values has {values.Length}; they must match");
        }

        var payload = new AtkValue[values.Length];
        var described = new JArray();

        for (var i = 0; i < values.Length; i++)
        {
            var requested = types.Length > 0 ? types[i] : "int";
            var text = strings.Length > 0 ? strings[i] : string.Empty;

            switch (requested.ToLowerInvariant())
            {
                case "int":
                    payload[i].Type = AtkValueType.Int;
                    payload[i].Int = values[i];
                    described.Add(new JObject { ["type"] = "int", ["value"] = values[i] });
                    break;

                case "uint":
                    payload[i].Type = AtkValueType.UInt;
                    payload[i].UInt = unchecked((uint)values[i]);
                    described.Add(new JObject { ["type"] = "uint", ["value"] = unchecked((uint)values[i]) });
                    break;

                case "bool":
                    payload[i].Type = AtkValueType.Bool;
                    payload[i].Byte = values[i] == 0 ? (byte)0 : (byte)1;
                    described.Add(new JObject { ["type"] = "bool", ["value"] = values[i] != 0 });
                    break;

                case "float":
                    payload[i].Type = AtkValueType.Float;
                    payload[i].Float = values[i];
                    described.Add(new JObject { ["type"] = "float", ["value"] = (float)values[i] });
                    break;

                case "string":
                    payload[i].SetManagedString(text);
                    described.Add(new JObject { ["type"] = "string", ["value"] = text });
                    break;

                default:
                    return Json.ToolError(
                        $"unknown type '{requested}' at index {i}; use int, uint, bool, float or string");
            }
        }

        var accepted = false;
        string? failure = null;

        try
        {
            fixed (AtkValue* first = payload)
            {
                accepted = unit->FireCallback((uint)payload.Length, payload.Length == 0 ? null : first, close);
            }
        }
        catch (Exception ex)
        {
            failure = ex.GetType().Name + ": " + ex.Message;
        }

        if (failure is not null)
        {
            return Json.ToolError($"firing the callback on addon '{name}' failed: {failure}");
        }

        return new JObject
        {
            ["addon"] = name,
            ["addonId"] = unit->Id,
            ["valueCount"] = payload.Length,
            ["values"] = described,
            ["close"] = close,
            ["fired"] = true,
            ["callbackAccepted"] = accepted,
        };
    }

    private static unsafe object ContentsFinderState()
    {
        var result = new JObject();

        unsafe
        {
            var agent = AgentContentsFinder.Instance();
            if (agent is not null)
            {
                result["agent"] = new JObject
                {
                    ["addonId"] = agent->AddonId,
                    ["selectedDuty"] = new JObject
                    {
                        ["contentType"] = agent->SelectedDuty.ContentType.ToString(),
                        ["id"] = agent->SelectedDuty.Id,
                    },
                    ["hasRouletteSelected"] = agent->HasRouletteSelected,
                    ["dutyPenaltyMinutes"] = agent->DutyPenaltyMinutes,
                    ["listChanged"] = agent->ListChanged,
                    ["isAddonReady"] = agent->IsAddonReady(),
                    ["isAddonShown"] = agent->IsAddonShown(),
                    ["isAddonHidden"] = agent->IsAddonHidden(),
                    ["selectedDutyCount"] = agent->ContentList.Count,
                };
            }
            else
            {
                result["agent"] = null;
            }

            var finder = ContentsFinder.Instance();
            if (finder is not null)
            {
                result["finder"] = new JObject
                {
                    ["isUnrestrictedParty"] = finder->IsUnrestrictedParty,
                    ["isMinimalIL"] = finder->IsMinimalIL,
                    ["isSilenceEcho"] = finder->IsSilenceEcho,
                    ["isExplorerMode"] = finder->IsExplorerMode,
                    ["isLevelSync"] = finder->IsLevelSync,
                    ["isLimitedLevelingRoulette"] = finder->IsLimitedLevelingRoulette,
                };

                var info = finder->GetQueueInfo();
                if (info is not null)
                {
                    result["queue"] = new JObject
                    {
                        ["queuedClassJobId"] = info->QueuedClassJobId,
                        ["enteredQueueTimestamp"] = info->EnteredQueueTimestamp,
                        ["queuedContentRouletteId"] = info->QueuedContentRouletteId,
                        ["poppedContentIsInProgress"] = info->PoppedContentIsInProgress,
                        ["poppedContentIsUnrestrictedParty"] = info->PoppedContentIsUnrestrictedParty,
                        ["poppedContentIsLevelSync"] = info->PoppedContentIsLevelSync,
                    };
                }
                else
                {
                    result["queue"] = null;
                }
            }
            else
            {
                result["finder"] = null;
            }

            var main = GameMain.Instance();
            if (main is not null)
            {
                result["gameMain"] = new JObject
                {
                    ["currentContentFinderConditionId"] = main->CurrentContentFinderConditionId,
                    ["currentTerritoryTypeId"] = main->CurrentTerritoryTypeId,
                };
            }
            else
            {
                result["gameMain"] = null;
            }
        }

        return result;
    }

    private static unsafe object OpenDutyFinderDuty(JObject args)
    {
        var cfc = args["cfc"]?.Value<uint>() ?? 0u;
        if (cfc == 0u)
        {
            return Json.ToolError("cfc must be the ContentFinderCondition row id of the duty to select");
        }

        var unrestricted = args["unrestricted"]?.Value<bool>();
        var hideIfShown = args["hideIfShown"]?.Value<bool>() ?? false;
        var notes = new JArray();
        string? failure = null;

        unsafe
        {
            var agent = AgentContentsFinder.Instance();
            if (agent is null)
            {
                return Json.ToolError("the duty finder agent is not available yet");
            }

            if (unrestricted is { } flag)
            {
                var finder = ContentsFinder.Instance();
                if (finder is null)
                {
                    return Json.ToolError(
                        "the unrestricted party flag cannot be set because the duty finder state is not available yet");
                }

                if (finder->IsUnrestrictedParty != flag)
                {
                    finder->IsUnrestrictedParty = flag;
                    notes.Add($"unrestricted party set to {flag}");
                }
            }

            try
            {
                agent->OpenRegularDuty(cfc, hideIfShown);
            }
            catch (Exception ex)
            {
                failure = ex.GetType().Name + ": " + ex.Message;
            }
        }

        if (failure is not null)
        {
            return Json.ToolError($"selecting duty {cfc} through the duty finder agent failed: {failure}");
        }

        return new JObject
        {
            ["requested"] = cfc,
            ["opened"] = true,
            ["hideIfShown"] = hideIfShown,
            ["notes"] = notes,
        };
    }

    /// <summary>
    /// Reads the duty finder's own list component rather than the addon's AtkValues: the
    /// value table only ever holds the rows the native list decided to push out, while
    /// <c>DutyList-&gt;Items</c> is the full tree the list is actually driving callbacks from.
    /// </summary>
    private static unsafe object DutyList(JObject args)
    {
        var maxItems = Math.Clamp(args["maxItems"]?.Value<int>() ?? 200, 1, 2000);
        var includeNames = args["includeNames"]?.Value<bool>() ?? true;

        var addonName = args["addon"]?.Value<string>() ?? "ContentsFinder";
        var unit = UiTools.ResolveAddon(addonName, args["addonId"]);
        if (unit is null)
        {
            return Json.ToolError(
                "no loaded addon named 'ContentsFinder' was found; open the duty finder first, or pass the addon's raw addonId.");
        }

        var list = ((AddonContentsFinder*)unit)->DutyList;
        if (list is null)
        {
            return Json.ToolError("the duty finder duty list is not loaded yet; open the duty finder first");
        }

        var items = list->Items;
        var total = items.Count;
        var selectedIndex = list->SelectedItemIndex;

        // AutoDuty's HeadersCount: the number of section headers strictly above the selected
        // row. It has to be counted over the whole list, not just the returned window.
        var headersAboveSelected = 0;
        for (var i = 0; i < selectedIndex && i < total; i++)
        {
            var probe = items[i].Value;
            if (probe is null)
            {
                continue;
            }

            if (IsSectionHeader(probe))
            {
                headersAboveSelected++;
            }
        }

        var entries = new JArray();
        var shown = Math.Min(total, maxItems);
        for (var i = 0; i < shown; i++)
        {
            var item = items[i].Value;
            if (item is null)
            {
                continue;
            }

            var entry = new JObject
            {
                ["index"] = i,
                ["header"] = IsSectionHeader(item),
                ["hidden"] = item->IsHidden,
                ["depth"] = item->Depth,
                ["height"] = item->Height,
                ["state"] = item->State.ToString(),
                ["type"] = item->Type.ToString(),
            };

            if (item->UIntValues.Count > 0)
            {
                entry["uint"] = item->UIntValues[0];
            }

            if (includeNames)
            {
                var name = ReadItemText(item);
                if (!string.IsNullOrEmpty(name))
                {
                    entry["name"] = name;
                }
            }

            if (i == selectedIndex)
            {
                entry["selected"] = true;
            }

            entries.Add(entry);
        }

        return new JObject
        {
            ["total"] = total,
            ["shown"] = shown,
            ["truncated"] = total > shown,
            ["selectedIndex"] = selectedIndex,
            ["headersAboveSelected"] = headersAboveSelected,
            ["sectionCallbackValue"] = headersAboveSelected + 1,
            ["entries"] = entries,
        };
    }

    /// <summary>
    /// Mirrors AutoDuty's <c>SelectDuty</c>: callback 3 on the duty finder is the list's
    /// "activate the group my row belongs to" event, and the game wants the ordinal of that
    /// group among the section headers, one based.
    /// </summary>
    private static unsafe object SelectDutyListGroup(JObject args)
    {
        var addonName = args["addon"]?.Value<string>() ?? "ContentsFinder";
        var unit = UiTools.ResolveAddon(addonName, args["addonId"]);

        if (unit is null)
        {
            var hideIfShown = args["hideIfShown"]?.Value<bool>();
            if (hideIfShown is null)
            {
                return Json.ToolError(
                    "the duty finder window is not loaded; pass hideIfShown and cfc to open it first");
            }

            var agent = AgentContentsFinder.Instance();
            if (agent is null)
            {
                return Json.ToolError("the duty finder agent is not available yet");
            }

            var cfc = args["cfc"]?.Value<uint>() ?? 0u;
            if (cfc == 0u)
            {
                return Json.ToolError("cfc is required when the duty finder window has to be opened first");
            }

            agent->OpenRegularDuty(cfc, hideIfShown.Value);

            return new JObject
            {
                ["opened"] = true,
                ["requested"] = cfc,
                ["note"] = "the duty finder was opened; call this again once the window is ready",
            };
        }

        var addon = (AddonContentsFinder*)unit;
        var list = addon->DutyList;
        if (list is null)
        {
            return Json.ToolError("the duty finder duty list is not loaded yet");
        }

        var items = list->Items;
        var total = items.Count;
        var selectedIndex = list->SelectedItemIndex;

        var headersAbove = 0;
        for (var i = 0; i < selectedIndex && i < total; i++)
        {
            var probe = items[i].Value;
            if (probe is not null && IsSectionHeader(probe))
            {
                headersAbove++;
            }
        }

        var section = (uint)(headersAbove + 1);
        var payload = stackalloc AtkValue[2];
        payload[0].Type = AtkValueType.Int;
        payload[0].Int = 3;
        payload[1].Type = AtkValueType.UInt;
        payload[1].UInt = section;

        var accepted = unit->FireCallback(2, payload, true);

        return new JObject
        {
            ["addon"] = unit->NameString,
            ["addonId"] = unit->Id,
            ["selectedIndex"] = selectedIndex,
            ["headersAboveSelected"] = headersAbove,
            ["sectionCallbackValue"] = section,
            ["fired"] = true,
            ["callbackAccepted"] = accepted,
        };
    }

    /// <summary>AutoDuty treats a first uint value of 0 or 1 as a section header row.</summary>
    private static unsafe bool IsSectionHeader(AtkComponentTreeListItem* item) =>
        item->UIntValues.Count > 0 && (item->UIntValues[0] == 0u || item->UIntValues[0] == 1u);

    /// <summary>
    /// Reads a list row's label. The row's own string values are preferred; when they are
    /// empty the renderer's text nodes are walked by field access only, so nothing here
    /// calls a reverse P/Invoke that could fault on a stale pointer.
    /// </summary>
    private static unsafe string? ReadItemText(AtkComponentTreeListItem* item)
    {
        for (var i = 0; i < item->StringValues.Count; i++)
        {
            var s = item->StringValues[i];
            if (s.HasValue)
            {
                var text = s.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        var renderer = item->Renderer;
        if (renderer is null)
        {
            return null;
        }

        var uld = &renderer->UldManager;
        var count = Math.Min((int)uld->NodeListCount, 128);
        for (var i = 0; i < count; i++)
        {
            var node = uld->NodeList[i];
            if (node is null || node->Type != NodeType.Text)
            {
                continue;
            }

            var text = ((AtkTextNode*)node)->NodeText.ToString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    private static int[] ReadIntArray(JToken? token)
    {
        return token switch
        {
            null => Array.Empty<int>(),
            JArray array => array.Select(t => t.Value<int>()).ToArray(),
            JObject => Array.Empty<int>(),
            _ => new[] { token.Value<int>() },
        };
    }

    private static string[] ReadStringArray(JToken? token)
    {
        return token switch
        {
            null => Array.Empty<string>(),
            JArray array => array.Select(t => t.Value<string>() ?? string.Empty).ToArray(),
            JObject => Array.Empty<string>(),
            _ => new[] { token.Value<string>() ?? string.Empty },
        };
    }
}
