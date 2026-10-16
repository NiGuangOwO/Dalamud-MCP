using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Plugin;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Managed developer-plugin bridge. Shape-checked against the installed Dalamud;
/// never loads an arbitrary assembly into MCP's own load context or executes C# snippets.
/// </summary>
internal static class DevPluginTools
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.AddAsync("load_dev_plugin", "Register and load a local development plugin",
            "Registers an absolute plugin DLL and its sibling manifest in Dalamud's developer locations, scans and loads it through the installed plugin manager, then reports the actual load state. Does not assume that sending a chat command succeeded. Existing development plugins are reused; a different installed path with the same internal name is refused. Uses version-checked managed reflection because Dalamud has no public loader API. Does not reload the MCP host.",
            Json.Schema(
                ("filePath", "string", "Absolute path to a local plugin DLL with a sibling JSON manifest.", true),
                ("autoReload", "boolean", "Watch the build output for automatic reload (default true).", false)),
            args => Load(svc, args), mutating: true);
    }

    private static object Service(Assembly assembly, string name)
    {
        var type = assembly.GetType(name) ?? throw new ToolException($"unsupported Dalamud: missing {name}");
        var service = assembly.GetType("Dalamud.Service`1")?.MakeGenericType(type);
        return service?.GetMethod("Get", All, Type.EmptyTypes)?.Invoke(null, null)
            ?? throw new ToolException($"Dalamud service unavailable: {name}");
    }

    private static MethodInfo Method(object target, string name, int parameterCount) =>
        target.GetType().GetMethods(All).SingleOrDefault(m => m.Name == name && m.GetParameters().Length == parameterCount)
        ?? throw new ToolException($"unsupported Dalamud: missing {target.GetType().Name}.{name}/{parameterCount}");

    private static object? Property(object target, string name) => target.GetType().GetProperty(name, All)?.GetValue(target);

    internal static async Task<object?> Load(GameServices svc, JObject args)
    {
        var path = args.Value<string>("filePath");
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            !Path.GetExtension(path).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("filePath must be an absolute DLL path");
        path = Path.GetFullPath(path);
        if (!File.Exists(path)) throw new ToolException("plugin DLL does not exist");
        var manifestPath = Path.ChangeExtension(path, ".json");
        if (!File.Exists(manifestPath)) throw new ToolException("sibling plugin manifest does not exist");
        var manifest = JObject.Parse(File.ReadAllText(manifestPath));
        var name = manifest.Value<string>("InternalName");
        if (string.IsNullOrWhiteSpace(name) || name.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("invalid internal name or attempted self load");
        var assembly = typeof(IDalamudPluginInterface).Assembly;
        var api = (int?)assembly.GetType("Dalamud.Plugin.Internal.PluginManager")?.GetProperty("DalamudApiLevel", All)?.GetValue(null);
        if (manifest.Value<int?>("DalamudApiLevel") != api) throw new ToolException("plugin manifest API does not match this Dalamud");

        await Gate.WaitAsync();
        try
        {
            var manager = Service(assembly, "Dalamud.Plugin.Internal.PluginManager");
            var config = Service(assembly, "Dalamud.Configuration.Internal.DalamudConfiguration");
            var scan = Method(manager, "ScanDevPluginsAsync", 0);
            var save = Method(config, "QueueSave", 0);
            var locations = Property(config, "DevPluginLoadLocations") as IList
                ?? throw new ToolException("unsupported Dalamud developer-location collection");
            var locationType = assembly.GetType("Dalamud.Configuration.DevPluginLocationSettings")
                ?? throw new ToolException("unsupported Dalamud developer-location settings");
            object[] Plugins() => (Property(manager, "InstalledPlugins") as IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
            var existing = Plugins().FirstOrDefault(p => string.Equals(Property(p, "InternalName") as string, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null && (Property(existing, "DllFile") as FileInfo)?.FullName != path)
                throw new ToolException("a plugin with this internal name is already installed at another path");

            await svc.Framework.RunOnFrameworkThread(() =>
            {
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                var location = locations.Cast<object>().FirstOrDefault(p => string.Equals(Property(p, "Path") as string, path, StringComparison.OrdinalIgnoreCase));
                if (location == null)
                {
                    location = Activator.CreateInstance(locationType)!;
                    locationType.GetProperty("Path")!.SetValue(location, path);
                    locations.Add(location);
                }
                locationType.GetProperty("IsEnabled")!.SetValue(location, true);
                save.Invoke(config, null);
            });
            var scanTask = (Task)(scan.Invoke(manager, null) ?? throw new ToolException("scan returned no task"));
            await scanTask;
            var plugin = Plugins().FirstOrDefault(p => string.Equals((Property(p, "DllFile") as FileInfo)?.FullName, path, StringComparison.OrdinalIgnoreCase))
                ?? throw new ToolException("plugin was not registered after the developer scan; check Dalamud logs");
            if (Property(plugin, "IsDev") is not true) throw new ToolException("refusing to control a non-development plugin");
            var autoReload = plugin.GetType().GetProperty("AutomaticReload", All)
                ?? throw new ToolException("unsupported Dalamud developer auto-reload setting");
            autoReload.SetValue(plugin, args.Value<bool?>("autoReload") ?? true);
            if (Property(plugin, "IsLoaded") is not true)
            {
                if (!svc.Config.AllowMutatingTools) throw new ToolException("mutating tools disabled");
                var load = Method(plugin, "LoadAsync", 3);
                var task = (Task)load.Invoke(plugin, new object[] { PluginLoadReason.Installer, false, CancellationToken.None })!;
                await task;
            }
            var loaded = Property(plugin, "IsLoaded") is true;
            if (!loaded) throw new ToolException($"plugin load did not complete: {Property(plugin, "State")}");
            return new JObject { ["internalName"] = name, ["filePath"] = path, ["isLoaded"] = loaded, ["isDev"] = true, ["autoReload"] = (bool)autoReload.GetValue(plugin)! };
        }
        catch (TargetInvocationException ex) { throw new ToolException($"developer plugin operation failed: {ex.InnerException?.Message ?? ex.Message}"); }
        finally { Gate.Release(); }
    }
}
