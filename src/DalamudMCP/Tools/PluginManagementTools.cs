using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Read-only inventory of the plugins Dalamud knows about, served straight from the
/// public <see cref="IDalamudPluginInterface.InstalledPlugins"/> contract rather than
/// through reflection over Dalamud internals. Loading, unloading and reloading are
/// deliberately not exposed here: <c>manage_plugin</c> already routes those through the
/// game's own chat command, which keeps a single code path for lifecycle changes.
/// </summary>
internal static class PluginManagementTools
{
    private const int MaxLimit = 100;

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "plugin_list",
            "List installed plugins",
            "Lists the plugins Dalamud reports, with their load state and manifest summary. " +
            "Use the cursor from the previous page to fetch the next one.",
            Json.Schema(
                ("query", "string", "Optional: only plugins whose name or internal name contains this text", false),
                ("cursor", "integer", "Opaque offset returned by the previous page", false),
                ("limit", "integer", $"Plugins per page (1-{MaxLimit}, default 50)", false)),
            args => List(svc, args));

        registry.Add(
            "plugin_describe",
            "Describe an installed plugin",
            "Reports one plugin's manifest in full: author, description, version, repository, " +
            "tags and load state.",
            Json.Schema(
                ("pluginName", "string", "Plugin display name or internal name (case-insensitive)", true)),
            args => Describe(svc, args));
    }

    private static IReadOnlyList<IExposedPlugin> Installed(GameServices svc)
    {
        var plugins = svc.PluginInterface?.InstalledPlugins;
        if (plugins is null) return Array.Empty<IExposedPlugin>();

        var list = new List<IExposedPlugin>();
        foreach (var plugin in plugins)
        {
            if (plugin is null) continue;
            list.Add(plugin);
        }

        list.Sort((a, b) => string.Compare(a?.Name ?? string.Empty, b?.Name ?? string.Empty, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    private static JObject List(GameServices svc, JObject args)
    {
        var all = Installed(svc);
        var query = Blank(args.Value<string?>("query"));

        IEnumerable<IExposedPlugin> matching = all;
        if (query is not null)
        {
            matching = all.Where(p =>
                Contains(p.Name, query) ||
                Contains(p.InternalName, query));
        }

        var filtered = matching.ToList();
        var cursor = Math.Max(0, args.Value<int?>("cursor") ?? 0);
        var limit = Math.Clamp(args.Value<int?>("limit") ?? 50, 1, MaxLimit);
        var page = filtered.Skip(cursor).Take(limit).ToList();
        var next = cursor + page.Count;

        return new JObject
        {
            ["count"] = page.Count,
            ["total"] = filtered.Count,
            ["installedTotal"] = all.Count,
            ["cursor"] = cursor,
            ["nextCursor"] = next < filtered.Count ? next : null,
            ["plugins"] = new JArray(page.Select(Summary)),
        };
    }

    private static JObject Describe(GameServices svc, JObject args)
    {
        var name = Blank(args.Value<string>("pluginName"));
        if (name is null) throw new ToolException("pluginName is required");

        var match = Installed(svc).FirstOrDefault(p =>
            string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.InternalName, name, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            match = Installed(svc).FirstOrDefault(p => Contains(p.Name, name) || Contains(p.InternalName, name));
        }

        if (match is null)
            throw new ToolException($"no installed plugin matches '{name}'");

        var manifest = match.Manifest;
        return new JObject
        {
            ["name"] = match.Name,
            ["internalName"] = match.InternalName,
            ["version"] = match.Version?.ToString(),
            ["isLoaded"] = match.IsLoaded,
            ["isDev"] = match.IsDev,
            ["isTesting"] = match.IsTesting,
            ["isThirdParty"] = match.IsThirdParty,
            ["isBanned"] = match.IsBanned,
            ["isOutdated"] = match.IsOutdated,
            ["isOrphaned"] = match.IsOrphaned,
            ["isDecommissioned"] = match.IsDecommissioned,
            ["hasMainUi"] = match.HasMainUi,
            ["hasConfigUi"] = match.HasConfigUi,
            ["manifest"] = new JObject
            {
                ["name"] = manifest?.Name,
                ["internalName"] = manifest?.InternalName,
                ["author"] = manifest?.Author,
                ["description"] = manifest?.Description,
                ["punchline"] = manifest?.Punchline,
                ["repoUrl"] = manifest?.RepoUrl,
                ["installedFromUrl"] = manifest?.InstalledFromUrl,
                ["dalamudApiLevel"] = manifest?.DalamudApiLevel,
                ["minimumDalamudVersion"] = manifest?.MinimumDalamudVersion?.ToString(),
                ["tags"] = manifest is null ? null : new JArray(manifest.Tags ?? new List<string>()),
                ["supportsProfiles"] = manifest?.SupportsProfiles,
                ["canUnloadAsync"] = manifest?.CanUnloadAsync,
                ["lastUpdate"] = manifest?.LastUpdate,
            },
        };
    }

    private static JObject Summary(IExposedPlugin p) => new()
    {
        ["name"] = p.Name,
        ["internalName"] = p.InternalName,
        ["version"] = p.Version?.ToString(),
        ["isLoaded"] = p.IsLoaded,
        ["isDev"] = p.IsDev,
        ["isTesting"] = p.IsTesting,
        ["isThirdParty"] = p.IsThirdParty,
        ["hasMainUi"] = p.HasMainUi,
        ["hasConfigUi"] = p.HasConfigUi,
        ["author"] = p.Manifest?.Author,
        ["punchline"] = p.Manifest?.Punchline,
    };

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool Contains(string? value, string needle) =>
        value is not null && value.Contains(needle, StringComparison.OrdinalIgnoreCase);
}
