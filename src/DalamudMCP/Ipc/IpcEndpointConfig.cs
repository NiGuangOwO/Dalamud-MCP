namespace DalamudMCP.Ipc;

/// <summary>
/// Persisted form of a declared IPC endpoint. The live <see cref="IpcEndpoint"/> is
/// what the invoker uses; this serializable twin is what the plugin config stores.
/// </summary>
public sealed class IpcEndpointConfig
{
    public string PluginName { get; set; } = string.Empty;

    public string MethodName { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

    public string? Description { get; set; }
}
