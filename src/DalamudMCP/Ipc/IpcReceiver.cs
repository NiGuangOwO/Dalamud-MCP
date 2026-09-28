using System;
using Dalamud.Plugin;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Ipc;

/// <summary>
/// Subscribes to the plugin's push channel: any plugin can publish a payload tagged
/// with a key, and MCP clients drain it later through <c>query_push_data</c>.
/// Malformed payloads are stored under the key <c>raw</c> instead of being dropped, so
/// a buggy producer is still debuggable.
/// </summary>
public sealed class IpcReceiver : IDisposable
{
    private const string ChannelName = "DalamudMCP.PushData";

    private readonly PushDataBuffer buffer;
    private readonly Dalamud.Plugin.Ipc.ICallGateProvider<string, object?> provider;
    private bool disposed;

    public IpcReceiver(IDalamudPluginInterface pi, PushDataBuffer buffer)
    {
        this.buffer = buffer;
        provider = pi.GetIpcProvider<string, object?>(ChannelName);
        provider.RegisterFunc(OnPushData);
    }

    /// <summary>Channel name another plugin subscribes/publishes against.</summary>
    public static string Name => ChannelName;

    private object? OnPushData(string payload)
    {
        string key;
        string data;
        try
        {
            var parsed = JObject.Parse(payload);
            key = parsed.Value<string>("key") ?? string.Empty;
            data = parsed.ContainsKey("data") ? parsed["data"]!.ToString(Newtonsoft.Json.Formatting.None) : payload;
        }
        catch
        {
            key = "raw";
            data = payload;
        }

        if (string.IsNullOrEmpty(key)) key = "raw";
        buffer.Push(key, data);
        return null;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            provider.UnregisterFunc();
        }
        catch
        {
            // Unregistering after plugin teardown can race; nothing to do about it.
        }
    }
}
