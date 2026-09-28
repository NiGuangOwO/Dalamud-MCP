using System;
using System.Collections.Generic;
using System.Reflection;
using Dalamud.Game.ClientState;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Utility.Signatures;
using Lumina.Excel;

namespace DalamudMCP;

/// <summary>
/// Everything the tool handlers need, resolved once from Dalamud's DI container.
/// Tool handlers receive this instead of reaching for statics so the plugin stays testable
/// and so a late-initialized service cannot be used before the client is ready.
/// </summary>
public sealed class GameServices
{
    public required IPluginLog Log { get; init; }
    public required IFramework Framework { get; init; }
    public required IClientState ClientState { get; init; }
    public required IObjectTable ObjectTable { get; init; }
    public required IPartyList PartyList { get; init; }
    public required IPlayerState PlayerState { get; init; }
    public required ITargetManager TargetManager { get; init; }
    public required ICondition Condition { get; init; }
    public required IDataManager DataManager { get; init; }
    public required IGameGui GameGui { get; init; }
    public required IChatGui ChatGui { get; init; }
    public required ICommandManager CommandManager { get; init; }
    public required IUnlockState UnlockState { get; init; }
    public required IFateTable FateTable { get; init; }
    public required IToastGui ToastGui { get; init; }
    public required ISeStringEvaluator SeStringEvaluator { get; init; }
    public required IGameInventory GameInventory { get; init; }
    public required IAetheryteList AetheryteList { get; init; }
    public required ISigScanner SigScanner { get; init; }
    public required IGameInteropProvider Interop { get; init; }
    public required IBuddyList BuddyList { get; init; }
    public required IDutyState DutyState { get; init; }

    /// <summary>
    /// The plugin interface is used by a few tools to persist config changes made
    /// through MCP (event collection settings, registered IPC endpoints).
    /// </summary>
    public IDalamudPluginInterface? PluginInterface { get; init; }

    public Configuration Config { get; init; } = new();

    /// <summary>Shorthand for an Excel sheet lookup.</summary>
    public ExcelSheet<T> Sheet<T>() where T : struct, Lumina.Excel.IExcelRow<T> =>
        DataManager.GetExcelSheet<T>();

    // ------------------------------------------------------------------
    // Runtime-typed sheet access
    //
    // Sheets are generic over their row struct, but the object-data tools take a sheet
    // name as a string, so the type argument is only known at run time. These thin
    // wrappers exist so that reflection only has to reach a single well-known method.
    // ------------------------------------------------------------------

    /// <summary>Resolves a sheet name (or row type name) to its generated row struct.</summary>
    private static readonly Lazy<IReadOnlyDictionary<string, Type>> SheetRowTypes =
        new(BuildSheetRowTypeMap, isThreadSafe: true);

    private static IReadOnlyDictionary<string, Type> BuildSheetRowTypeMap()
    {
        var map = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);

        foreach (var type in typeof(Lumina.Excel.Sheets.Item).Assembly.GetTypes())
        {
            if (!type.IsValueType || !type.IsPublic) continue;

            // FullName, not Name: Name is the simple name ("Item"), so a namespace prefix
            // test against it never matches and the map would come back empty.
            if (type.FullName is not { } fullName ||
                !fullName.StartsWith("Lumina.Excel.Sheets.", StringComparison.Ordinal))
            {
                continue;
            }

            // The duplicate Lumina.Excel.Sheets.Experimental.* set is excluded by the prefix
            // above, so column names cannot shift under the tools.
            if (fullName.StartsWith("Lumina.Excel.Sheets.Experimental.", StringComparison.Ordinal)) continue;

            var attr = type.GetCustomAttribute<Lumina.Excel.SheetAttribute>();
            if (attr?.Name is not { Length: > 0 } sheetName) continue;

            // SheetAttribute.Name is the on-disk sheet name; the type name usually matches,
            // but the attribute is authoritative and lets both spellings resolve.
            map[sheetName] = type;
            map[type.Name] = type;
        }

        return map;
    }

    /// <summary>Returns the generated row struct for a sheet name, or null when unknown.</summary>
    public static Type? ResolveRowType(string sheetName) =>
        SheetRowTypes.Value.TryGetValue(sheetName, out var t) ? t : null;

    /// <summary>All known sheet names (the generated set, not the loaded-file set).</summary>
    public static IReadOnlyCollection<string> KnownSheetNames =>
        (IReadOnlyCollection<string>)SheetRowTypes.Value.Keys;

    /// <summary>Strongly typed sheet, resolved from a run-time <see cref="Type"/>.</summary>
    public object? TypedSheet(Type rowType) =>
        typeof(GameServices).GetMethod(nameof(Sheet))!
            .MakeGenericMethod(rowType)
            .Invoke(this, null);

    /// <summary>Strongly typed subrow sheet, resolved from a run-time <see cref="Type"/>.</summary>
    public object? TypedSubrowSheet(Type rowType) =>
        typeof(GameServices).GetMethod(nameof(SubrowSheet))!
            .MakeGenericMethod(rowType)
            .Invoke(this, null);

    /// <summary>Shorthand for a subrow Excel sheet lookup.</summary>
    public Lumina.Excel.SubrowExcelSheet<T> SubrowSheet<T>() where T : struct, Lumina.Excel.IExcelSubrow<T> =>
        DataManager.GetSubrowExcelSheet<T>();

    /// <summary>The loaded raw sheet for a name, or null when the client lacks it.</summary>
    public RawExcelSheet? RawSheet(string name) => DataManager.Excel.GetRawSheet(name);

    public Action<string> LogInfo => m => Log.Information("[DalamudMCP] {Message}", m);
    public Action<string> LogDebug => m => Log.Debug("[DalamudMCP] {Message}", m);
    public Action<string> LogError => m => Log.Error("[DalamudMCP] {Message}", m);
}
