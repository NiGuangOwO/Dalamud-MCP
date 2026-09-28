using System;
using System.Collections.Generic;
using System.Linq;

namespace DalamudMCP.Ipc;

/// <summary>A declared IPC endpoint another plugin exposes for MCP callers.</summary>
public sealed class IpcEndpoint
{
    public string PluginName { get; set; } = string.Empty;

    public string MethodName { get; set; } = string.Empty;

    public string Signature { get; set; } = string.Empty;

    public string? Description { get; set; }
}

/// <summary>
/// The signature templates this plugin knows how to invoke over Dalamud IPC.
/// A plugin author registers an endpoint declaring one of these; the tool layer then
/// binds the matching generic <c>ICallGate</c> at call time.
/// </summary>
public static class IpcSignatures
{
    public const string FuncBool = "Func<bool>";
    public const string FuncString = "Func<string>";
    public const string FuncIntString = "Func<int, string>";
    public const string ActionBool = "Action<bool>";
    public const string FuncBoolString = "Func<bool, string>";

    public static readonly string[] Supported =
    {
        FuncBool, FuncString, FuncIntString, ActionBool, FuncBoolString,
    };

    public static bool IsSupported(string signature) =>
        Supported.Any(s => s.Equals(signature, StringComparison.OrdinalIgnoreCase));

    public static string? Normalize(string signature) =>
        Supported.FirstOrDefault(s => s.Equals(signature, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Thread-safe registry of declared endpoints, keyed by plugin.method.</summary>
public sealed class IpcEndpointRegistry
{
    private readonly object gate = new();
    private readonly List<IpcEndpoint> endpoints = new();

    /// <summary>The persisted plugin config may pre-populate this list.</summary>
    public IpcEndpointRegistry(IEnumerable<IpcEndpoint>? initial = null)
    {
        if (initial is not null) endpoints.AddRange(initial);
    }

    public static string Key(IpcEndpoint e) => $"{e.PluginName}.{e.MethodName}";

    /// <summary>Adds or replaces an endpoint after validation. Throws on bad input.</summary>
    public IpcEndpoint Register(string pluginName, string methodName, string signature, string? description)
    {
        if (string.IsNullOrWhiteSpace(pluginName))
            throw new ArgumentException("pluginName must not be empty");
        if (string.IsNullOrWhiteSpace(methodName))
            throw new ArgumentException("methodName must not be empty");
        var normalized = IpcSignatures.Normalize(signature)
            ?? throw new ArgumentException($"unsupported signature '{signature}'. Supported: {string.Join(", ", IpcSignatures.Supported)}");
        if (pluginName.Any(char.IsWhiteSpace) || methodName.Any(char.IsWhiteSpace))
            throw new ArgumentException("pluginName and methodName must not contain whitespace");

        lock (gate)
        {
            endpoints.RemoveAll(e => string.Equals(Key(e), $"{pluginName}.{methodName}", StringComparison.OrdinalIgnoreCase));
            var endpoint = new IpcEndpoint
            {
                PluginName = pluginName,
                MethodName = methodName,
                Signature = normalized,
                Description = string.IsNullOrWhiteSpace(description) ? null : description,
            };
            endpoints.Add(endpoint);
            return endpoint;
        }
    }

    public List<IpcEndpoint> List(string? pluginName = null)
    {
        lock (gate)
        {
            return endpoints
                .Where(e => string.IsNullOrEmpty(pluginName) || e.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase))
                .Select(e => new IpcEndpoint
                {
                    PluginName = e.PluginName,
                    MethodName = e.MethodName,
                    Signature = e.Signature,
                    Description = e.Description,
                })
                .ToList();
        }
    }

    public IpcEndpoint? Find(string pluginName, string methodName)
    {
        lock (gate)
        {
            return endpoints.FirstOrDefault(e =>
                e.PluginName.Equals(pluginName, StringComparison.OrdinalIgnoreCase) &&
                e.MethodName.Equals(methodName, StringComparison.OrdinalIgnoreCase));
        }
    }
}
