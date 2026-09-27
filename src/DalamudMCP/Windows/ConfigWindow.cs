using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace DalamudMCP.Windows;

/// <summary>
/// Settings window, opened with <c>/dalamudmcp</c> or the plugin-installer config button.
///
/// Every edit is written straight into <see cref="Configuration"/>; persistence happens only
/// when a control changes, so an accidental text edit in a slider does not write the config
/// file on every frame.
/// </summary>
public sealed class ConfigWindow : Window
{
    private static readonly Vector4 OkColor = new(0.45f, 0.85f, 0.45f, 1.0f);
    private static readonly Vector4 WarnColor = new(0.95f, 0.75f, 0.35f, 1.0f);

    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Dalamud MCP###DalamudMCPConfig")
    {
        this.plugin = plugin;

        Size = new Vector2(620, 520);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 320),
            MaximumSize = new Vector2(1400, 1200),
        };
    }

    public override void Draw()
    {
        var config = plugin.Config;
        var needsRestart = false;

        DrawStatus();

        ImGui.Separator();
        ImGui.Spacing();

        // ---------------------------------------------------------- listener
        var enabled = config.Enabled;
        if (ImGui.Checkbox("Enabled", ref enabled))
        {
            config.Enabled = enabled;
            Persist();
        }

        ImGui.SameLine();
        var autoStart = config.AutoStart;
        if (ImGui.Checkbox("Auto-start with the plugin", ref autoStart))
        {
            config.AutoStart = autoStart;
            Persist();
        }

        var port = config.Port;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("Port", ref port))
        {
            // Clamped here rather than on save so the field cannot show a bogus value.
            config.Port = Math.Clamp(port, 1024, 65535);
            Persist();
            needsRestart = true;
        }

        ImGui.TextDisabled("Bound to 127.0.0.1 only - the listener is never reachable from the network.");

        ImGui.Spacing();

        var timeout = (float)config.FrameworkTimeoutSeconds;
        ImGui.SetNextItemWidth(240);
        if (ImGui.SliderFloat("Framework timeout (s)", ref timeout, 0.5f, 60.0f, "%.1f"))
        {
            config.FrameworkTimeoutSeconds = timeout;
            Persist();
            needsRestart = true;
        }

        ImGui.TextDisabled("How long a tool call waits for the game's framework thread before failing.");

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ------------------------------------------------------------ safety
        ImGui.Text("Agent access");

        var mutating = config.AllowMutatingTools;
        if (ImGui.Checkbox("Allow mutating tools", ref mutating))
        {
            config.AllowMutatingTools = mutating;
            Persist();
        }

        ImGui.SameLine();
        if (config.AllowMutatingTools)
        {
            ImGui.TextColored(Pack(WarnColor), "agents may change game state");
        }
        else
        {
            ImGui.TextDisabled("read-only (recommended)");
        }

        ImGui.TextDisabled("When off, tools that change game state are hidden from and rejected for clients.");

        ImGui.Spacing();

        var token = config.AuthToken ?? string.Empty;
        ImGui.SetNextItemWidth(320);
        if (ImGui.InputText("Auth token", ref token, 256))
        {
            config.AuthToken = token.Trim();
            Persist();
        }

        if (string.IsNullOrEmpty(config.AuthToken))
        {
            ImGui.TextDisabled("Empty = no authentication. Any local process can reach the port.");
        }
        else
        {
            ImGui.TextDisabled("Clients must send: Authorization: Bearer <token>");
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ----------------------------------------------------------- limits
        ImGui.Text("Limits");

        var maxObjects = config.MaxObjectResults;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("Max object results", ref maxObjects))
        {
            config.MaxObjectResults = Math.Clamp(maxObjects, 1, 5000);
            Persist();
        }

        var maxBytes = config.MaxMemoryReadBytes;
        ImGui.SetNextItemWidth(140);
        if (ImGui.InputInt("Max memory read (bytes)", ref maxBytes, 1024, 16384))
        {
            config.MaxMemoryReadBytes = Math.Clamp(maxBytes, 16, 1024 * 1024);
            Persist();
        }

        var logRequests = config.LogRequests;
        if (ImGui.Checkbox("Log every MCP request", ref logRequests))
        {
            config.LogRequests = logRequests;
            Persist();
        }

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // ------------------------------------------------------------ tools
        if (ImGui.Button("Save now"))
        {
            plugin.SaveConfig();
            plugin.Notify("settings saved");
        }

        ImGui.SameLine();
        if (IsRunning())
        {
            if (ImGui.Button("Restart listener"))
            {
                plugin.SaveConfig();
                plugin.StartServer();
                needsRestart = false;
                plugin.Notify("listener restarted");
            }

            ImGui.SameLine();
            if (ImGui.Button("Stop listener"))
            {
                plugin.StopServer();
                needsRestart = false;
            }
        }
        else
        {
            if (ImGui.Button("Start listener"))
            {
                plugin.SaveConfig();
                plugin.StartServer();
                needsRestart = false;
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Open config folder"))
        {
            // Presented as a convenience; failures are logged rather than thrown, since a
            // missing shell association must not take down the window.
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = plugin.PluginInterface.ConfigDirectory.FullName,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                plugin.LogError($"could not open config folder: {ex.Message}");
            }
        }

        if (needsRestart && IsRunning())
        {
            ImGui.Spacing();
            ImGui.TextColored(Pack(WarnColor), "Port and timeout changes apply after the listener restarts.");
        }

        ImGui.Spacing();
        ImGui.TextDisabled($"Tools registered: {plugin.ToolCount}. Use /dalamudmcp tools to list them.");
    }

    /// <summary>
    /// Writes the config to disk once the user stops editing a field, not on every frame
    /// the control reports a change - otherwise dragging a slider rewrites the file
    /// dozens of times per second.
    /// </summary>
    private void Persist()
    {
        if (ImGui.IsItemDeactivatedAfterEdit()) plugin.SaveConfig();
    }

    private void DrawStatus()
    {
        var running = IsRunning();
        var endpoint = plugin.Endpoint;

        ImGui.Text("Status:");
        ImGui.SameLine();

        if (running)
        {
            ImGui.TextColored(Pack(OkColor), "listening");
            ImGui.SameLine();
            ImGui.Text($"at {endpoint}");
        }
        else
        {
            ImGui.TextColored(Pack(WarnColor), "stopped");
            ImGui.SameLine();
            ImGui.Text($"- would listen on {endpoint}");
        }

        ImGui.TextDisabled(
            $"tools: {plugin.ToolCount}   sessions: {plugin.ActiveSessions}   " +
            $"auth: {(string.IsNullOrEmpty(plugin.Config.AuthToken) ? "off" : "on")}");
    }

    private bool IsRunning() => plugin.IsRunning;

    /// <summary>ImGui colours are packed 0xAABBGGRR on the integer widget overloads.</summary>
    private static uint Pack(Vector4 c) =>
        ((uint)(c.W * 255f) << 24) | ((uint)(c.Z * 255f) << 16) | ((uint)(c.Y * 255f) << 8) | (uint)(c.X * 255f);
}
