using System;
using System.Linq;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Fates;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Party;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using DalamudMCP.Mcp;
using DalamudMCP.Tools;
using DalamudMCP.Windows;

namespace DalamudMCP;

/// <summary>
/// Plugin entry point.
///
/// Live object graph:
///   Plugin
///     GameServices   - the Dalamud services every tool handler reads from
///     ToolRegistry   - the registered tools, with the mutating-tool gate wired to config
///     GameThread     - marshals every tool call onto the game's framework thread
///     McpServer      - the HTTP/MCP transport on 127.0.0.1:Port
///     ConfigWindow   - ImGui settings window (WindowSystem-managed)
///
/// The services arrive by constructor injection: Dalamud's container picks the constructor
/// whose parameters it can all resolve, so listing them is the registration.
/// </summary>
public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/dalamudmcp";

    private readonly IDalamudPluginInterface pluginInterface;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly IChatGui chatGui;
    private readonly ICommandManager commandManager;
    private readonly IToastGui toastGui;

    private readonly GameServices services;
    private readonly ToolRegistry registry;
    private readonly WindowSystem windowSystem = new("DalamudMCP");
    private readonly ConfigWindow configWindow;

    // Recreated whenever the listener restarts so a changed port or framework timeout
    // takes effect without a plugin reload.
    private GameThread? gameThread;
    private McpServer? server;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        IObjectTable objectTable,
        IPartyList partyList,
        IPlayerState playerState,
        ITargetManager targetManager,
        ICondition condition,
        IDataManager dataManager,
        IGameGui gameGui,
        IChatGui chatGui,
        ICommandManager commandManager,
        IUnlockState unlockState,
        IFateTable fateTable,
        IToastGui toastGui,
        ISeStringEvaluator seStringEvaluator,
        IGameInventory gameInventory,
        IAetheryteList aetheryteList,
        ISigScanner sigScanner)
    {
        this.pluginInterface = pluginInterface;
        this.log = log;
        this.framework = framework;
        this.chatGui = chatGui;
        this.commandManager = commandManager;
        this.toastGui = toastGui;

        Config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Sanitize();

        // Pick the string table before anything user-facing is constructed. "auto"
        // follows the game client's language so a Chinese client gets Chinese UI.
        Localization.Initialize(Config.Language, clientState.ClientLanguage.ToString());

        services = new GameServices
        {
            Log = log,
            Framework = framework,
            ClientState = clientState,
            ObjectTable = objectTable,
            PartyList = partyList,
            PlayerState = playerState,
            TargetManager = targetManager,
            Condition = condition,
            DataManager = dataManager,
            GameGui = gameGui,
            ChatGui = chatGui,
            CommandManager = commandManager,
            UnlockState = unlockState,
            FateTable = fateTable,
            ToastGui = toastGui,
            SeStringEvaluator = seStringEvaluator,
            GameInventory = gameInventory,
            AetheryteList = aetheryteList,
            SigScanner = sigScanner,
            Config = Config,
        };

        registry = new ToolRegistry { AllowMutating = () => Config.AllowMutatingTools };
        ClientTools.Register(registry, services);
        ObjectTools.Register(registry, services);
        DataTools.Register(registry, services);
        MemoryTools.Register(registry, services);
        StructTools.Register(registry, services);
        UiTools.Register(registry, services);

        configWindow = new ConfigWindow(this);
        windowSystem.AddWindow(configWindow);

        pluginInterface.UiBuilder.Draw += windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;

        commandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = Localization.T("cmd.help"),
            ShowInHelp = true,
        });

        if (Config.Enabled && Config.AutoStart) StartServer();
        else LogInfo($"listener not started (enabled={Config.Enabled}, autoStart={Config.AutoStart})");
    }

    public Configuration Config { get; }

    /// <summary>Exposed so the settings window can read the config directory.</summary>
    public IDalamudPluginInterface PluginInterface => pluginInterface;

    /// <summary>The live MCP server, or null while the listener is stopped.</summary>
    public McpServer? Server => server;

    public ToolRegistry Registry => registry;

    public bool IsRunning => server?.IsRunning == true;

    public int ActiveSessions => server?.ActiveSessions ?? 0;

    public int ToolCount => registry.Tools.Count;

    /// <summary>Endpoint an MCP client should connect to.</summary>
    public string Endpoint => $"http://127.0.0.1:{Config.Port}/mcp";

    public void SaveConfig()
    {
        Config.Sanitize();
        Config.Save(pluginInterface);
    }

    public void OpenConfig() => configWindow.IsOpen = true;

    /// <summary>Chat feedback for settings-window actions.</summary>
    public void Notify(string message) => Print(message);

    public void LogError(string message) => log.Error("[DalamudMCP] {Message}", message);

    /// <summary>Starts (or restarts) the listener on the configured port.</summary>
    public void StartServer()
    {
        StopServer();

        try
        {
            gameThread = new GameThread(framework, LogDebug, TimeSpan.FromSeconds(Config.FrameworkTimeoutSeconds));
            server = new McpServer(registry, gameThread, LogDebug)
            {
                // Read live so editing the token takes effect without a restart.
                TokenProvider = () => string.IsNullOrEmpty(Config.AuthToken) ? null : Config.AuthToken,
                LogRequests = () => Config.LogRequests,
            };

            server.Start(Config.Port);
            LogInfo($"MCP server listening on http://127.0.0.1:{server.Port}/mcp ({registry.Tools.Count} tools)");
        }
        catch (Exception ex)
        {
            LogError($"failed to start MCP server on port {Config.Port}: {ex.GetType().Name}: {ex.Message}");
            toastGui.ShowError(Localization.T("toast.bindFailed", Config.Port));

            server?.Dispose();
            server = null;
            gameThread?.Dispose();
            gameThread = null;
        }
    }

    public void StopServer()
    {
        if (server is not null)
        {
            try
            {
                server.Stop();
                server.Dispose();
            }
            catch (Exception ex)
            {
                LogError($"error while stopping MCP server: {ex.GetType().Name}: {ex.Message}");
            }

            server = null;
            LogInfo("MCP server stopped");
        }

        gameThread?.Dispose();
        gameThread = null;
    }

    private void OnCommand(string command, string arguments)
    {
        var argument = (arguments ?? string.Empty).Trim();
        var verb = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        switch (verb.Length > 0 ? verb[0].ToLowerInvariant() : string.Empty)
        {
            case "":
            case "config":
            case "settings":
                OpenConfig();
                break;

            case "start":
                StartServer();
                Print(IsRunning
                    ? Localization.T("cmd.listeningOn", Endpoint)
                    : Localization.T("cmd.startFailed", Config.Port));
                break;

            case "stop":
                StopServer();
                Print(Localization.T("cmd.stopped"));
                break;

            case "restart":
                StartServer();
                Print(IsRunning ? Localization.T("cmd.restartedOn", Endpoint) : Localization.T("cmd.restartFailed"));
                break;

            case "status":
                Print(IsRunning
                    ? Localization.T("cmd.statusRunning", Endpoint, ToolCount, ActiveSessions,
                        string.IsNullOrEmpty(Config.AuthToken) ? Localization.T("ui.authOnOffOff") : Localization.T("ui.authOnOff"))
                    : Localization.T("cmd.statusStopped", Config.Port));
                break;

            case "port":
                if (verb.Length > 1 && int.TryParse(verb[1], out var port) && port is >= 1024 and <= 65535)
                {
                    Config.Port = port;
                    SaveConfig();
                    if (IsRunning) StartServer();
                    Print(Localization.T("cmd.portSet", port,
                        IsRunning ? Localization.T("cmd.portRestarted") : Localization.T("cmd.portNotRunning")));
                }
                else
                {
                    Print(Localization.T("cmd.portUsage", CommandName));
                }

                break;

            case "tools":
                Print(Localization.T("cmd.toolsRegistered", ToolCount,
                    string.Join(", ", registry.Tools.Select(t => t.Name))));
                break;

            default:
                Print(Localization.T("cmd.unknownSubcommand", verb[0]));
                break;
        }
    }

    private void Print(string message) => chatGui.Print($"[Dalamud MCP] {message}", "DalamudMCP", null);

    private void LogInfo(string message) => log.Information("[DalamudMCP] {Message}", message);

    private void LogDebug(string message) => log.Debug("[DalamudMCP] {Message}", message);

    public void Dispose()
    {
        commandManager.RemoveHandler(CommandName);

        pluginInterface.UiBuilder.Draw -= windowSystem.Draw;
        pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;

        windowSystem.RemoveAllWindows();
        StopServer();
    }
}
