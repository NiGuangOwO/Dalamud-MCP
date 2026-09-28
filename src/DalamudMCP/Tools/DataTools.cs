using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DalamudMCP.Mcp;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Read-only access to the game's Excel data sheets (items, actions, quests, ...).
///
/// Sheet rows are value types generated per sheet, so the type argument is only known at
/// run time. Every lookup therefore goes through <see cref="GameServices.ResolveRowType"/>,
/// which is driven by the <c>SheetAttribute</c> that Lumina's source generator stamps onto
/// each row struct — that attribute is authoritative, so both the on-disk sheet name and
/// the CLR type name resolve.
/// </summary>
internal static class DataTools
{
    /// <summary>Columns never worth serializing — raw row plumbing.</summary>
    private static readonly HashSet<string> SkipColumns = new(StringComparer.Ordinal)
    {
        "ExcelPage", "RowOffset",
    };

    /// <summary>Cap on columns pulled out of one row, so a wide sheet cannot flood a response.</summary>
    private const int MaxColumnsPerRow = 120;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "search_game_data",
            "Search game data",
            "Searches a game data sheet (Excel) by text. Example sheets: Item, Action, Status, Quest, " +
            "Mount, Companion, Emote, Recipe, CraftAction, Trait, TerritoryType, World, ClassJob, " +
            "InstanceContent, ContentFinderCondition, ENpcResident, BNpcName, PlaceName, Aetheryte. " +
            "Use list_game_data_sheets to discover names and get_game_data_sheet_info to see which " +
            "columns are searchable.",
            Json.Schema(
                ("sheet", "string", "The sheet name, e.g. 'Item'", true),
                ("query", "string", "Text to look for in the sheet's text columns", true),
                ("exact", "boolean", "Require an exact (case-insensitive) match instead of a substring (default false)", false),
                ("max", "integer", "Maximum rows to return (default 25, max 200)", false)),
            args => SearchGameData(svc, args));

        registry.Add(
            "get_game_data_row",
            "Get game data row",
            "Reads one row from a game data sheet by its row id, returning every readable column. " +
            "Row references to other sheets are returned as {\"rowId\": n} rather than expanded.",
            Json.Schema(
                ("sheet", "string", "The sheet name, e.g. 'Item'", true),
                ("rowId", "integer", "The row id", true)),
            args => GetGameDataRow(svc, args));

        registry.Add(
            "list_game_data_sheets",
            "List game data sheets",
            "Lists Excel sheet names available to the plugin, optionally filtered by a substring. " +
            "There are over a thousand sheets, so pass a filter when you know roughly what you want.",
            Json.Schema(
                ("filter", "string", "Only return sheets whose name contains this text", false),
                ("max", "integer", "Maximum names to return (default 200, max 2000)", false)),
            args => ListSheets(svc, args));

        registry.Add(
            "get_game_data_sheet_info",
            "Get game data sheet info",
            "Describes a sheet: row count, column count, and the searchable text columns. Use this " +
            "before search_game_data to learn which fields carry readable text.",
            Json.Schema(
                ("sheet", "string", "The sheet name, e.g. 'Item'", true)),
            args => SheetInfo(svc, args));

        registry.Add(
            "get_item",
            "Get item",
            "Looks up an item by id or by name and returns its common fields: name, description, item " +
            "level, equip level, rarity, category, stack size, and vendor price.",
            Json.Schema(
                ("itemId", "integer", "Item id", false),
                ("name", "string", "Item name (case-insensitive exact match, or substring if contains=true)", false),
                ("contains", "boolean", "Treat 'name' as a substring search (default false)", false)),
            args => GetItem(svc, args));

        registry.Add(
            "get_action",
            "Get action",
            "Looks up an action (ability/spell) by id or name: name, description, level, job, cast and " +
            "recast times, range, and cost. The description lives on the companion ActionTransient sheet, " +
            "so it is fetched separately and may be absent.",
            Json.Schema(
                ("actionId", "integer", "Action id", false),
                ("name", "string", "Action name (case-insensitive)", false)),
            args => GetAction(svc, args));

        registry.Add(
            "get_status",
            "Get status",
            "Looks up a status (buff/debuff) by id or name: name, description, max stacks, and " +
            "the flags that control dispelling and movement locks.",
            Json.Schema(
                ("statusId", "integer", "Status id", false),
                ("name", "string", "Status name (case-insensitive)", false)),
            args => GetStatus(svc, args));

        registry.Add(
            "get_territory",
            "Get territory",
            "Looks up a zone/territory by id or name: name, region, zone, map, whether it is a PvP zone, " +
            "and whether mounts are allowed.",
            Json.Schema(
                ("territoryId", "integer", "Territory type id", false),
                ("name", "string", "Territory name (case-insensitive substring)", false)),
            args => GetTerritory(svc, args));

        registry.Add(
            "get_class_job",
            "Get class/job",
            "Class and job definitions. Omit classJobId to list every class and job with its " +
            "abbreviation, which is the cheapest way to map a job id to a name.",
            Json.Schema(
                ("classJobId", "integer", "Class/job id", false)),
            args => GetClassJob(svc, args));
    }

    // ------------------------------------------------------------- handlers

    /// <summary>
    /// The client's Excel module. Dalamud hands this over as soon as the plugin loads, but it
    /// is null before the game data is ready, and a null here used to surface as a raw
    /// NullReferenceException instead of the phrased "not available yet" the tools promise.
    /// </summary>
    private static Lumina.Excel.ExcelModule Excel(GameServices svc) =>
        svc.DataManager.Excel
        ?? throw new ToolException("the game's sheet data is not loaded yet; start the client and log in");

    private static object ListSheets(GameServices svc, JObject args)
    {
        var filter = args["filter"]?.Value<string>();
        var max = Math.Clamp(args["max"]?.Value<int?>() ?? 200, 1, 2000);

        // The generated row types are the honest set: they are what every other tool can read.
        var known = GameServices.KnownSheetNames;
        var matched = known
            .Where(n => filter is null || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new JObject
        {
            ["knownSheetCount"] = known.Count,
            ["loadedSheetCount"] = Excel(svc).SheetNames.Count,
            ["matchedCount"] = matched.Count,
            ["truncated"] = matched.Count > max,
            ["sheets"] = new JArray(matched.Take(max).Select(n => (JToken)n)),
        };
    }

    private static object SheetInfo(GameServices svc, JObject args)
    {
        var name = RequireSheet(args);
        var rowType = GameServices.ResolveRowType(name)
                      ?? throw new ToolException($"unknown sheet '{name}' (see list_game_data_sheets)");

        var excel = Excel(svc);
        var loaded = excel.SheetNames.Contains(name);
        var result = new JObject
        {
            ["sheet"] = name,
            ["rowType"] = rowType.FullName,
            ["isLoadedByClient"] = loaded,
        };

        var raw = excel.GetRawSheet(name);
        if (raw is not null)
        {
            result["rowCount"] = raw.Count;

            var cols = new JArray();
            for (var i = 0; i < raw.Columns.Count; i++)
            {
                var c = raw.Columns[i];
                cols.Add(new JObject
                {
                    ["index"] = i,
                    ["type"] = c.Type.ToString(),
                    ["offset"] = c.Offset,
                });
            }

            result["columnCount"] = raw.Columns.Count;
            result["columns"] = cols;
        }
        else
        {
            result["rowCount"] = null;
        }

        // PropertyInfo objects are not JSON-serializable (Newtonsoft throws
        // "Could not determine JSON object type for type RuntimePropertyInfo"); emit the names.
        result["textColumns"] = new JArray(TextColumns(rowType).Select(p => (JToken)p.Name));
        result["propertyCount"] = rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance).Length;
        return result;
    }

    private static object SearchGameData(GameServices svc, JObject args)
    {
        var name = RequireSheet(args);
        var query = args["query"]?.Value<string>();
        if (string.IsNullOrEmpty(query)) throw new ToolException("missing required parameter: query");

        var exact = args["exact"]?.Value<bool?>() ?? false;
        var max = Math.Clamp(args["max"]?.Value<int?>() ?? 25, 1, 200);

        var rowType = GameServices.ResolveRowType(name)
                      ?? throw new ToolException($"unknown sheet '{name}' (see list_game_data_sheets)");

        var sheet = OpenSheet(svc, name, rowType)
                    ?? throw new ToolException($"sheet '{name}' could not be opened");

        var textProps = TextColumns(rowType);
        if (textProps.Count == 0)
            throw new ToolException($"sheet '{name}' has no text columns to search");

        var matches = new JArray();
        var total = 0;
        var truncated = false;

        foreach (var row in (IEnumerable)sheet)
        {
            if (row is null) continue;

            var matched = false;
            foreach (var p in textProps)
            {
                var text = TryReadText(p, row);
                if (string.IsNullOrEmpty(text)) continue;

                if (exact
                        ? string.Equals(text, query, StringComparison.OrdinalIgnoreCase)
                        : text.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (!matched) continue;

            total++;
            if (matches.Count < max)
            {
                matches.Add(RowToJson(row, rowType));
            }
            else
            {
                truncated = true;
            }
        }

        return new JObject
        {
            ["sheet"] = name,
            ["query"] = query,
            ["count"] = matches.Count,
            ["totalMatching"] = total,
            ["truncated"] = truncated,
            ["rows"] = matches,
        };
    }

    private static object GetGameDataRow(GameServices svc, JObject args)
    {
        var name = RequireSheet(args);
        var rowId = args["rowId"]?.Value<uint?>()
                    ?? throw new ToolException("missing required parameter: rowId");

        var rowType = GameServices.ResolveRowType(name)
                      ?? throw new ToolException($"unknown sheet '{name}' (see list_game_data_sheets)");

        var sheet = OpenSheet(svc, name, rowType)
                    ?? throw new ToolException($"sheet '{name}' could not be opened");

        var tryGetRow = sheet.GetType().GetMethod("TryGetRow", new[] { typeof(uint), rowType.MakeByRefType() })
                        ?? throw new ToolException($"sheet '{name}' exposes no row reader");

        var buffer = new object?[2];
        buffer[0] = rowId; // MethodInfo.Invoke turns a null into default(uint) silently — rowId must be boxed explicitly or every call reads row 0.
        buffer[1] = Activator.CreateInstance(rowType); // TryGetRow's out param is a struct

        bool found;
        try
        {
            found = (bool)tryGetRow.Invoke(sheet, buffer)!;
        }
        catch (TargetInvocationException tie)
        {
            throw new ToolException($"sheet '{name}' row {rowId} could not be read: {tie.InnerException?.Message}");
        }

        if (!found || buffer[1] is null)
            throw new ToolException($"sheet '{name}' has no row {rowId}");

        var o = RowToJson(buffer[1]!, rowType);
        o["sheet"] = name;
        return o;
    }

    private static object GetItem(GameServices svc, JObject args)
    {
        var sheet = svc.Sheet<Item>();
        var itemId = args["itemId"]?.Value<uint?>();
        var name = args["name"]?.Value<string>();
        var contains = args["contains"]?.Value<bool?>() ?? false;

        Item row;
        if (itemId is { } id)
        {
            if (!sheet.TryGetRow(id, out row)) throw new ToolException($"no item with id {id}");
        }
        else if (!string.IsNullOrEmpty(name))
        {
            var found = FindByName(sheet, r => r.Name.ExtractText(), name, contains)
                        ?? throw new ToolException($"no item named '{name}'");
            row = found;
        }
        else
        {
            throw new ToolException("provide itemId or name");
        }

        return new JObject
        {
            ["itemId"] = row.RowId,
            ["name"] = row.Name.ExtractText(),
            ["singular"] = row.Singular.ExtractText(),
            ["description"] = row.Description.ExtractText(),
            ["levelItem"] = row.LevelItem.RowId,
            ["levelEquip"] = row.LevelEquip,
            ["rarity"] = row.Rarity,
            ["itemAction"] = row.ItemAction.RowId,
            ["itemUICategory"] = Conv.RowRefSummary(row.ItemUICategory, r => r.Name.ExtractText()),
            ["itemSearchCategory"] = Conv.RowRefSummary(row.ItemSearchCategory, r => r.Name.ExtractText()),
            ["classJobCategory"] = Conv.RowRefSummary(row.ClassJobCategory, r => r.Name.ExtractText()),
            ["stackSize"] = row.StackSize,
            ["canBeHq"] = row.CanBeHq,
            ["isUntradable"] = row.IsUntradable,
            ["priceMid"] = row.PriceMid,
            ["priceLow"] = row.PriceLow,
            ["isCollectable"] = row.IsCollectable,
            ["alwaysCollectable"] = row.AlwaysCollectable,
        };
    }

    private static object GetAction(GameServices svc, JObject args)
    {
        var sheet = svc.Sheet<Lumina.Excel.Sheets.Action>();
        var actionId = args["actionId"]?.Value<uint?>();
        var name = args["name"]?.Value<string>();

        Lumina.Excel.Sheets.Action row;
        if (actionId is { } id)
        {
            if (!sheet.TryGetRow(id, out row)) throw new ToolException($"no action with id {id}");
        }
        else if (!string.IsNullOrEmpty(name))
        {
            row = FindByName(sheet, r => r.Name.ExtractText(), name, contains: false)
                  ?? throw new ToolException($"no action named '{name}'");
        }
        else
        {
            throw new ToolException("provide actionId or name");
        }

        // The player-facing description is not on Action; it lives on ActionTransient.
        var description = svc.Sheet<ActionTransient>().GetRowOrDefault(row.RowId)?.Description.ExtractText();

        return new JObject
        {
            ["actionId"] = row.RowId,
            ["name"] = row.Name.ExtractText(),
            ["description"] = Conv.NullIfEmpty(description),
            ["classJob"] = Conv.RowRefSummary(row.ClassJob, r => r.Abbreviation.ExtractText()),
            ["classJobLevel"] = row.ClassJobLevel,
            ["cast100ms"] = row.Cast100ms,
            ["recast100ms"] = row.Recast100ms,
            ["range"] = row.Range,
            ["effectRange"] = row.EffectRange,
            ["primaryCostType"] = row.PrimaryCostType,
            ["primaryCostValue"] = row.PrimaryCostValue,
            ["secondaryCostType"] = row.SecondaryCostType,
            ["secondaryCostValue"] = row.SecondaryCostValue.RowId,
            ["actionCategory"] = Conv.RowRefSummary(row.ActionCategory, r => r.Name.ExtractText()),
            ["targetArea"] = row.TargetArea,
            ["isPvP"] = row.IsPvP,
            ["isPlayerAction"] = row.IsPlayerAction,
        };
    }

    private static object GetStatus(GameServices svc, JObject args)
    {
        var sheet = svc.Sheet<Status>();
        var statusId = args["statusId"]?.Value<uint?>();
        var name = args["name"]?.Value<string>();

        Status row;
        if (statusId is { } id)
        {
            if (!sheet.TryGetRow(id, out row)) throw new ToolException($"no status with id {id}");
        }
        else if (!string.IsNullOrEmpty(name))
        {
            row = FindByName(sheet, r => r.Name.ExtractText(), name, contains: false)
                  ?? throw new ToolException($"no status named '{name}'");
        }
        else
        {
            throw new ToolException("provide statusId or name");
        }

        return new JObject
        {
            ["statusId"] = row.RowId,
            ["name"] = row.Name.ExtractText(),
            ["description"] = row.Description.ExtractText(),
            ["icon"] = row.Icon,
            ["maxStacks"] = row.MaxStacks,
            ["isFcBuff"] = row.IsFcBuff,
            ["statusCategory"] = row.StatusCategory,
            ["canDispel"] = row.CanDispel,
            ["isGaze"] = row.IsGaze,
            ["isPermanent"] = row.IsPermanent,
            ["partyListPriority"] = row.PartyListPriority,
            ["lockMovement"] = row.LockMovement,
            ["lockActions"] = row.LockActions,
        };
    }

    private static object GetTerritory(GameServices svc, JObject args)
    {
        var sheet = svc.Sheet<TerritoryType>();
        var territoryId = args["territoryId"]?.Value<uint?>();
        var name = args["name"]?.Value<string>();

        TerritoryType row;
        if (territoryId is { } id)
        {
            if (!sheet.TryGetRow(id, out row)) throw new ToolException($"no territory with id {id}");
        }
        else if (!string.IsNullOrEmpty(name))
        {
            row = FindByName(sheet, r => r.Name.ExtractText(), name, contains: true)
                  ?? throw new ToolException($"no territory matching '{name}'");
        }
        else
        {
            throw new ToolException("provide territoryId or name");
        }

        return new JObject
        {
            ["territoryId"] = row.RowId,
            ["name"] = row.Name.ExtractText(),
            ["placeName"] = Conv.RowRefSummary(row.PlaceName, r => r.Name.ExtractText()),
            ["placeNameZone"] = Conv.RowRefSummary(row.PlaceNameZone, r => r.Name.ExtractText()),
            ["placeNameRegion"] = Conv.RowRefSummary(row.PlaceNameRegion, r => r.Name.ExtractText()),
            ["map"] = Conv.RowRefSummary(row.Map, r => r.Id.ExtractText()),
            ["contentFinderCondition"] = Conv.RowRefSummary(row.ContentFinderCondition, r => r.Name.ExtractText()),
            ["aetheryte"] = Conv.RowRefSummary(row.Aetheryte, r => r.Singular.ExtractText()),
            ["exVersion"] = Conv.RowRefSummary(row.ExVersion, r => r.Name.ExtractText()),
            ["isPvpZone"] = row.IsPvpZone,
            ["mount"] = row.Mount,
            ["stealth"] = row.Stealth,
        };
    }

    private static object GetClassJob(GameServices svc, JObject args)
    {
        var sheet = svc.Sheet<ClassJob>();
        var id = args["classJobId"]?.Value<uint?>();

        if (id is null)
        {
            var all = new JArray();
            foreach (var r in sheet)
            {
                if (r.RowId == 0) continue;
                var name = r.Name.ExtractText();
                if (string.IsNullOrEmpty(name)) continue;
                all.Add(new JObject
                {
                    ["id"] = r.RowId,
                    ["name"] = name,
                    ["abbreviation"] = r.Abbreviation.ExtractText(),
                });
            }

            return new JObject { ["count"] = all.Count, ["classesAndJobs"] = all };
        }

        if (!sheet.TryGetRow(id.Value, out var row))
            throw new ToolException($"no class/job with id {id}");

        return new JObject
        {
            ["classJobId"] = row.RowId,
            ["name"] = row.Name.ExtractText(),
            ["abbreviation"] = row.Abbreviation.ExtractText(),
            ["nameEnglish"] = row.NameEnglish.ExtractText(),
            ["classJobCategory"] = Conv.RowRefSummary(row.ClassJobCategory, r => r.Name.ExtractText()),
            ["startingLevel"] = row.StartingLevel,
            ["canQueueForDuty"] = row.CanQueueForDuty,
            ["itemSoulCrystal"] = Conv.RowRefSummary(row.ItemSoulCrystal, r => r.Name.ExtractText()),
            ["unlockQuest"] = Conv.RowRefSummary(row.UnlockQuest, r => r.Name.ExtractText()),
        };
    }

    // ------------------------------------------------------------- sheet plumbing

    /// <summary>
    /// Opens a sheet generically. Some sheets are subrow-only, so the plain lookup is tried
    /// first and the subrow lookup is the fallback rather than an either/or decision.
    /// </summary>
    private static object? OpenSheet(GameServices svc, string name, Type rowType)
    {
        try
        {
            var sheet = svc.TypedSheet(rowType);
            if (sheet is not null) return sheet;
        }
        catch
        {
            // Subrow-only sheet; fall through.
        }

        try
        {
            return svc.TypedSubrowSheet(rowType);
        }
        catch
        {
            return null;
        }
    }

    private static List<PropertyInfo> TextColumns(Type rowType) =>
        rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(Lumina.Text.ReadOnly.ReadOnlySeString))
            .Where(p => p.GetIndexParameters().Length == 0)
            .OrderBy(p => p.Name, StringComparer.Ordinal)
            .ToList();

    private static string? TryReadText(PropertyInfo prop, object row)
    {
        try
        {
            return prop.GetValue(row) is Lumina.Text.ReadOnly.ReadOnlySeString s
                ? s.ExtractText()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static T? FindByName<T>(ExcelSheet<T> sheet, Func<T, string> nameOf, string name, bool contains)
        where T : struct, IExcelRow<T>
    {
        foreach (var candidate in sheet)
        {
            var n = nameOf(candidate);
            var match = contains
                ? n.Contains(name, StringComparison.OrdinalIgnoreCase)
                : string.Equals(n, name, StringComparison.OrdinalIgnoreCase);
            if (match) return candidate;
        }

        return null;
    }

    // ------------------------------------------------------------- row serialization

    /// <summary>
    /// Serializes a row by reflecting over its public scalar properties. Nested row
    /// references are emitted as <c>{ "rowId": n }</c> rather than expanded, so a single
    /// row response stays small enough to reason about.
    /// </summary>
    private static JObject RowToJson(object row, Type rowType)
    {
        var o = new JObject();
        var props = rowType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var written = 0;

        foreach (var p in props)
        {
            if (written >= MaxColumnsPerRow) break;
            if (SkipColumns.Contains(p.Name)) continue;
            if (p.GetIndexParameters().Length > 0) continue;

            object? value;
            try
            {
                value = p.GetValue(row);
            }
            catch
            {
                continue;
            }

            var v = ConvertValue(value);
            if (v is null) continue;

            o[p.Name] = v;
            written++;
        }

        return o;
    }

    private static JToken? ConvertValue(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            case Lumina.Text.ReadOnly.ReadOnlySeString se:
            {
                var text = se.ExtractText();
                return string.IsNullOrEmpty(text) ? null : text;
            }

            case string str:
                return string.IsNullOrEmpty(str) ? null : str;

            case bool b:
                return b;

            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return JToken.FromObject(value);

            case float f:
                return Math.Abs(f) < 0.0001f ? null : (JToken)Math.Round(f, 3);

            case Enum e:
                return e.ToString();

            default:
                break;
        }

        var type = value.GetType();

        // RowRef<T> — a value type carrying RowId. Report the id, never the target row.
        if (value is RowRef untypedRef)
            return untypedRef.RowId == 0 ? null : new JObject { ["rowId"] = untypedRef.RowId };

        var rowIdProp = type.GetProperty("RowId", BindingFlags.Public | BindingFlags.Instance);
        if (rowIdProp is not null && type.IsValueType)
        {
            var id = rowIdProp.GetValue(value);
            if (id is null) return null;
            var idValue = Convert.ToUInt32(id, System.Globalization.CultureInfo.InvariantCulture);
            return idValue == 0 ? null : new JObject { ["rowId"] = idValue };
        }

        // Collection<T> — headers, cost arrays, and similar fixed-size lists.
        if (value is IEnumerable en and not string)
        {
            var arr = new JArray();
            foreach (var item in en)
            {
                var v = ConvertValue(item);
                if (v is not null) arr.Add(v);
            }

            return arr.Count == 0 ? null : arr;
        }

        return null;
    }

    private static string RequireSheet(JObject args) =>
        args["sheet"]?.Value<string>() is { Length: > 0 } s
            ? s
            : throw new ToolException("missing required parameter: sheet");
}
