using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DalamudMCP.Mcp;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Chat delivery and plugin lifecycle commands, both routed through the chat box.
/// Every tool here is mutating: without the mutating-tool gate an agent cannot type
/// into the game at all.
/// </summary>
internal static unsafe class ChatTools
{
    public static void Register(ToolRegistry registry, GameServices svc)
    {
        registry.Add(
            "send_chat",
            "Send chat",
            "Sends a chat message through the chat box. Channels: say, yell, shout, party, alliance, " +
            "fc, tell (requires target), echo. Messages over 500 UTF-8 bytes are refused.",
            Json.Schema(
                ("message", "string", "Message body", true),
                ("channel", "string", "say/yell/shout/party/alliance/fc/tell/echo (default say)", false),
                ("target", "string", "Character name, only used with channel=tell", false)),
            args => SendChat(svc, args),
            mutating: true);

        registry.Add(
            "manage_plugin",
            "Manage plugin",
            "Loads, unloads or reloads a Dalamud plugin through its chat commands. action: load (needs " +
            "filePath), unload (needs pluginName), reload (needs both), all (reload every plugin). " +
            "Reloading this plugin would sever the MCP session, so it is refused.",
            Json.SchemaWithEnum(
                "action",
                new[] { "load", "unload", "reload", "all" },
                "Lifecycle operation",
                true,
                ("pluginName", "string", "Plugin InternalName (unload/reload)", false),
                ("filePath", "string", "Full path to the plugin DLL (load/reload)", false)),
            args => ManagePlugin(svc, args),
            mutating: true);

        registry.Add(
            "slash_command",
            "Run slash command",
            "Runs any game or plugin slash command exactly as if it had been typed into the chat box, " +
            "e.g. /duty finder, /target, /pcmd move, /vnav moveto. Use send_chat for ordinary chat and " +
            "this for commands. The leading slash is required.",
            Json.Schema(
                ("command", "string", "The full command line, starting with '/'", true)),
            args => SlashCommand(svc, args),
            mutating: true);
    }

    private static JObject SlashCommand(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var command = args.Value<string>("command")?.Trim() ?? string.Empty;
        if (command.Length == 0)
            throw new ToolException("command is required");
        if (!command.StartsWith('/'))
            throw new ToolException("command must start with '/'");
        if (command.Contains('\r') || command.Contains('\n'))
            throw new ToolException("command must not contain line breaks");
        if (Encoding.UTF8.GetByteCount(command) > 500)
            throw new ToolException("command exceeds 500 UTF-8 bytes");

        // Route through the command manager first so command aliases and plugin-registered
        // handlers resolve the same way they do for a typed line; fall back to the chat box
        // for anything the manager does not claim (macro-only or game-internal verbs).
        bool handled;
        try
        {
            handled = svc.CommandManager.ProcessCommand(command);
        }
        catch (Exception ex)
        {
            svc.LogDebug($"ProcessCommand threw for '{command}': {ex.GetType().Name}: {ex.Message}");
            handled = false;
        }

        if (!handled)
            handled = TrySend(svc, command);

        return new JObject
        {
            ["success"] = handled,
            ["command"] = command,
        };
    }

    private static JObject SendChat(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var message = args.Value<string>("message") ?? string.Empty;
        if (message.Length == 0)
            throw new ToolException("message is required");

        var channel = (args.Value<string?>("channel") ?? "say").ToLowerInvariant();
        var target = args.Value<string?>("target")?.Trim() ?? string.Empty;

        var prefix = channel switch
        {
            "say" => "/s ",
            "yell" => "/y ",
            "shout" => "/sh ",
            "party" => "/p ",
            "alliance" => "/a ",
            "fc" => "/fc ",
            "tell" => $"/tell {target} ",
            "echo" => "/e ",
            _ => throw new ToolException(
                $"unknown channel '{channel}'; expected say/yell/shout/party/alliance/fc/tell/echo"),
        };

        if (prefix.Contains("tell") && target.Length == 0)
            throw new ToolException("channel 'tell' requires target");

        var full = prefix + message;
        if (Encoding.UTF8.GetByteCount(full) > 500)
            throw new ToolException("message exceeds 500 UTF-8 bytes after the channel prefix");

        return new JObject
        {
            ["success"] = TrySend(svc, full),
            ["channel"] = channel,
            ["message"] = message,
        };
    }

    private static JObject ManagePlugin(GameServices svc, JObject args)
    {
        EnsureLoggedIn(svc);

        var action = (args.Value<string?>("action") ?? string.Empty).ToLowerInvariant();
        var pluginName = args.Value<string?>("pluginName")?.Trim() ?? string.Empty;
        var filePath = args.Value<string?>("filePath")?.Trim() ?? string.Empty;

        return action switch
        {
            "unload" => Unload(svc, pluginName),
            "load" => Load(svc, pluginName, filePath),
            "reload" => Reload(svc, pluginName, filePath),
            "all" => new JObject
            {
                ["success"] = TrySend(svc, "/xlplugins reload"),
                ["action"] = "reload_all",
            },
            _ => throw new ToolException($"unknown action '{action}'; expected load/unload/reload/all"),
        };
    }

    private static JObject Unload(GameServices svc, string pluginName)
    {
        EnsureValidPluginName(pluginName);
        EnsureNotSelf(svc, pluginName, "unload");
        return new JObject
        {
            ["success"] = TrySend(svc, $"/xlplugins disable {pluginName}"),
            ["action"] = "unload",
            ["pluginName"] = pluginName,
        };
    }

    private static JObject Load(GameServices svc, string pluginName, string filePath)
    {
        var full = ValidateDllPath(filePath);
        return new JObject
        {
            ["success"] = TrySend(svc, $"/xldev load \"{full}\""),
            ["action"] = "load",
            ["filePath"] = full,
        };
    }

    private static JObject Reload(GameServices svc, string pluginName, string filePath)
    {
        EnsureValidPluginName(pluginName);
        EnsureNotSelf(svc, pluginName, "reload");
        var full = ValidateDllPath(filePath);

        if (!TrySend(svc, $"/xlplugins disable {pluginName}"))
            throw new ToolException("failed to send the unload command");

        // The disable takes effect asynchronously; give the plugin manager a beat
        // before the load command goes out. No await here: handlers run on an
        // unsafe context, so the continuation chain stays fire-and-forget.
        _ = System.Threading.Tasks.Task.Delay(500).ContinueWith(
            _ => svc.Framework.RunOnFrameworkThread(() => TrySend(svc, $"/xldev load \"{full}\"")),
            System.Threading.Tasks.TaskScheduler.Default)
            .Unwrap()
            .ContinueWith(
                t => svc.LogError($"delayed plugin load failed: {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"),
                System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted);

        return new JObject
        {
            ["success"] = true,
            ["scheduled"] = true,
            ["action"] = "reload",
            ["pluginName"] = pluginName,
            ["filePath"] = full,
        };
    }

    // ------------------------------------------------------------------
    // Shared plumbing
    // ------------------------------------------------------------------

    /// <summary>Feeds a line through the chat box exactly as if the user had typed it.</summary>
    private static bool TrySend(GameServices svc, string message)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        if (bytes.Length > 500) return false;

        var uiModule = UIModule.Instance();
        if (uiModule is null) return false;

        using var utf8String = new Utf8String(bytes);
        uiModule->ProcessChatBoxEntry(&utf8String);
        return true;
    }

    private static void EnsureValidPluginName(string pluginName)
    {
        if (string.IsNullOrWhiteSpace(pluginName) ||
            pluginName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')))
        {
            throw new ToolException("pluginName may only contain letters, digits, '.', '_' and '-'");
        }
    }

    private static void EnsureNotSelf(GameServices svc, string pluginName, string verb)
    {
        if (pluginName.Equals("DalamudMCP", StringComparison.OrdinalIgnoreCase))
            throw new ToolException($"refusing to {verb} the plugin serving this MCP session");
    }

    private static string ValidateDllPath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || filePath.IndexOfAny(new[] { '\r', '\n', '"' }) >= 0)
            throw new ToolException("filePath is invalid");
        if (!System.IO.Path.IsPathRooted(filePath))
            throw new ToolException("filePath must be an absolute path");
        if (!System.IO.Path.GetExtension(filePath).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            throw new ToolException("filePath must be a .dll");
        if (!System.IO.File.Exists(filePath))
            throw new ToolException($"file not found: {filePath}");
        return System.IO.Path.GetFullPath(filePath);
    }

    private static void EnsureLoggedIn(GameServices svc)
    {
        if (!svc.ClientState.IsLoggedIn)
            throw new ToolException("not logged in");
    }
}
