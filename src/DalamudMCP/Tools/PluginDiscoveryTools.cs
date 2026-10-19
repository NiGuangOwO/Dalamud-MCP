using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>Discover supported entry points from public runtime registries, without target source.</summary>
internal static class PluginDiscoveryTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add("list_plugin_commands", "Discover registered plugin commands",
            "Reads the live Dalamud command registry and registered help without executing handlers. " +
            "Filter by command/help/handler assembly with query. Handler assembly is a hint, not proof of plugin ownership " +
            "(shared command frameworks can own wrappers). Hidden-help entries are included. Follow nextCursor.",
            Json.Schema(("query", "string", "Optional case-insensitive command/help/assembly substring (max 128 characters).", false),
                ("cursor", "integer", "Result offset (default 0).", false),
                ("limit", "integer", "Page size 1-100 (default 30).", false)),
            args => Commands(svc, args));
        registry.Add("open_plugin_ui", "Open a loaded plugin's supported UI",
            "Requests exactly one loaded plugin's public main/config UI callback through IExposedPlugin. " +
            "Exact internalName or display name only; ambiguous loaded names and unavailable callbacks are refused. " +
            "This opens UI only; requested=true does not prove a window appeared or a feature ran. Inspect addon/ImGui state afterward.",
            Json.SchemaWithEnum("ui", new[] { "main", "config" }, "Which supported UI to open (default main).", false,
                ("pluginName", "string", "Exact plugin internal or display name.", true)),
            args => Open(svc, args), mutating: true);
    }

    private static object Commands(GameServices svc, JObject args)
    {
        var query = args.Value<string>("query")?.Trim();
        var cursor = args.Value<int?>("cursor") ?? 0;
        var limit = args.Value<int?>("limit") ?? 30;
        if (query?.Length > 128 || cursor < 0 || limit is < 1 or > 100)
            throw new ToolException("query is limited to 128 characters; cursor >= 0; limit 1-100");
        var entries = svc.CommandManager.Commands;
        if (entries == null) return new JObject { ["available"] = false, ["reason"] = "command registry unavailable" };
        var rows = entries.Take(4096).Select(p => Command(p.Key, p.Value))
            .Where(p => string.IsNullOrEmpty(query) || new[] { "command", "help", "handlerAssembly" }
                .Any(k => p.Value<string>(k)?.Contains(query, StringComparison.OrdinalIgnoreCase) == true))
            .OrderBy(p => p.Value<string>("command"), StringComparer.Ordinal).ToArray();
        var page = rows.Skip(cursor).Take(limit).ToArray();
        return new JObject { ["available"] = true, ["total"] = rows.Length, ["count"] = page.Length,
            ["nextCursor"] = cursor + page.Length < rows.Length ? cursor + page.Length : null,
            ["scanIncomplete"] = entries.Count > 4096, ["commands"] = new JArray(page) };
    }

    internal static JObject Command(string name, IReadOnlyCommandInfo info)
    {
        var help = info.HelpMessage ?? "";
        return new JObject { ["command"] = name, ["help"] = help.Length > 4096 ? help[..4096] : help,
            ["helpTruncated"] = help.Length > 4096, ["showInHelp"] = info.ShowInHelp,
            ["handlerAssembly"] = info.Handler.Method.DeclaringType?.Assembly.GetName().Name };
    }

    private static object Open(GameServices svc, JObject args)
    {
        var name = args.Value<string>("pluginName");
        var ui = args.Value<string>("ui") ?? "main";
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || ui is not ("main" or "config"))
            throw new ToolException("pluginName (max 128 characters) and ui=main/config are required");
        var matches = svc.PluginInterface?.InstalledPlugins.Where(p => p != null && p.IsLoaded &&
            (string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))).Take(2).ToArray() ?? Array.Empty<IExposedPlugin>();
        if (matches.Length != 1) throw new ToolException("expected one exact loaded plugin match; use plugin_list");
        var plugin = matches[0];
        if (ui == "main" ? !plugin.HasMainUi : !plugin.HasConfigUi) throw new ToolException("this plugin has no supported " + ui + " UI callback");
        if (ui == "main") plugin.OpenMainUi(); else plugin.OpenConfigUi();
        return new JObject { ["internalName"] = plugin.InternalName, ["ui"] = ui, ["requested"] = true };
    }
}
