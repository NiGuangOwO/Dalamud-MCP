using System;

namespace DalamudMCP.Ipc;

/// <summary>
/// Aggregates the plugin-bridge subsystem: the push buffer, its IPC provider, and the
/// endpoint registry. Created on server start, disposed on stop so the IPC channel is
/// unregistered while the listener is down.
/// </summary>
public sealed class IpcHub : IDisposable
{
    public PushDataBuffer PushData { get; } = new();

    public IpcEndpointRegistry Endpoints { get; }

    public IpcReceiver? Receiver { get; private set; }

    public IpcHub(System.Collections.Generic.IEnumerable<IpcEndpoint>? initialEndpoints = null)
    {
        Endpoints = new IpcEndpointRegistry(initialEndpoints);
    }

    public void Start(Dalamud.Plugin.IDalamudPluginInterface pi)
    {
        Stop();
        Receiver = new IpcReceiver(pi, PushData);
    }

    public void Stop()
    {
        Receiver?.Dispose();
        Receiver = null;
    }

    public void Dispose() => Stop();
}
