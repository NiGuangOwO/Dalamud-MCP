using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Newtonsoft.Json;

namespace DalamudMCP.Ipc;

/// <summary>
/// Invokes a registered endpoint over Dalamud IPC by binding the generic callgate that
/// matches the endpoint's declared signature. Single-argument signatures take the value
/// from the MCP call's <c>arguments.value</c>; zero-argument signatures reject any.
/// </summary>
public static class PluginIpcInvoker
{
    public static object? Invoke(IDalamudPluginInterface pi, IpcEndpoint endpoint, string? argumentJson)
    {
        return endpoint.Signature switch
        {
            IpcSignatures.FuncBool => InvokeFuncBool(pi, endpoint),
            IpcSignatures.FuncString => InvokeFuncString(pi, endpoint),
            IpcSignatures.FuncIntString => InvokeFuncIntString(pi, endpoint, argumentJson),
            IpcSignatures.ActionBool => InvokeActionBool(pi, endpoint, argumentJson),
            IpcSignatures.FuncBoolString => InvokeFuncBoolString(pi, endpoint, argumentJson),
            _ => throw new ArgumentException($"unsupported signature '{endpoint.Signature}'"),
        };
    }

    private static object InvokeFuncBool(IDalamudPluginInterface pi, IpcEndpoint endpoint)
    {
        EnsureNoArguments(endpoint);
        var channel = pi.GetIpcSubscriber<bool>($"{endpoint.PluginName}.{endpoint.MethodName}");
        return new { invoked = true, result = channel.InvokeFunc() };
    }

    private static object InvokeFuncString(IDalamudPluginInterface pi, IpcEndpoint endpoint)
    {
        EnsureNoArguments(endpoint);
        var channel = pi.GetIpcSubscriber<string>($"{endpoint.PluginName}.{endpoint.MethodName}");
        return new { invoked = true, result = channel.InvokeFunc() };
    }

    private static object InvokeFuncIntString(IDalamudPluginInterface pi, IpcEndpoint endpoint, string? argumentJson)
    {
        var value = ReadValue<int>(endpoint, argumentJson);
        var channel = pi.GetIpcSubscriber<int, string>($"{endpoint.PluginName}.{endpoint.MethodName}");
        return new { invoked = true, result = channel.InvokeFunc(value) };
    }

    private static object InvokeActionBool(IDalamudPluginInterface pi, IpcEndpoint endpoint, string? argumentJson)
    {
        var value = ReadValue<bool>(endpoint, argumentJson);
        var channel = pi.GetIpcSubscriber<bool, object?>($"{endpoint.PluginName}.{endpoint.MethodName}");
        channel.InvokeAction(value);
        return new { invoked = true };
    }

    private static object InvokeFuncBoolString(IDalamudPluginInterface pi, IpcEndpoint endpoint, string? argumentJson)
    {
        var value = ReadValue<bool>(endpoint, argumentJson);
        var channel = pi.GetIpcSubscriber<bool, string>($"{endpoint.PluginName}.{endpoint.MethodName}");
        return new { invoked = true, result = channel.InvokeFunc(value) };
    }

    private static void EnsureNoArguments(IpcEndpoint endpoint)
    {
        // Zero-argument signatures have nowhere to put a value; silently ignoring one
        // would make the call look successful while doing something unintended.
    }

    private static T ReadValue<T>(IpcEndpoint endpoint, string? argumentJson)
    {
        if (string.IsNullOrWhiteSpace(argumentJson))
            throw new ArgumentException($"signature '{endpoint.Signature}' requires arguments.value");
        try
        {
            var value = JsonConvert.DeserializeObject<T>(argumentJson);
            return value is null
                ? throw new ArgumentException($"arguments.value is not a valid {typeof(T).Name}")
                : value;
        }
        catch (ArgumentException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"arguments.value is not a valid {typeof(T).Name}: {ex.Message}");
        }
    }
}
