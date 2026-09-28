using System;
using System.Linq;
using Dalamud.Plugin;
using DalamudMCP.Ipc;
using DalamudMCP.Mcp;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Tools;

/// <summary>
/// Bridges other plugins into MCP: declared endpoints can be invoked, and payloads
/// pushed over IPC can be drained. register_ipc_endpoint is the only mutating tool
/// here; the rest only read.
/// </summary>
internal static class PluginBridgeTools
{
    public static void Register(ToolRegistry registry, GameServices svc, IpcHub hub)
    {
        registry.Add(
            "query_push_data",
            "Query pushed data",
            "Reads payloads other plugins pushed over IPC (newest first). Pass key to filter by tag; " +
            "each entry has id, key, data (arbitrary JSON) and timestamp.",
            Json.Schema(
                ("key", "string", "Optional: only entries with this tag", false),
                ("count", "integer", "Max entries to return (default 50)", false)),
            args => QueryPushData(hub, args));

        registry.Add(
            "register_ipc_endpoint",
            "Register IPC endpoint",
            "Declares that another plugin exposes an IPC method MCP may call. " +
            "Supported signatures: Func<bool>, Func<string>, Func<int, string>, Action<bool>, Func<bool, string>. " +
            "Declaring the endpoint does not contact the other plugin; call_plugin_ipc does.",
            Json.SchemaWithEnum(
                "signature",
                IpcSignatures.Supported,
                "The IPC delegate shape the method implements",
                true,
                ("pluginName", "string", "InternalName of the target plugin", true),
                ("methodName", "string", "IPC name the target registered", true),
                ("description", "string", "Optional human-readable summary", false)),
            args => RegisterEndpoint(svc, hub, args),
            mutating: true);

        registry.Add(
            "call_plugin_ipc",
            "Call plugin IPC",
            "Invokes a previously registered endpoint. Signatures Func<bool> and Func<string> take no " +
            "argument; Func<int, string> expects arguments.value to be an integer; Action<bool> and " +
            "Func<bool, string> expect arguments.value to be a boolean.",
            Json.Schema(
                ("pluginName", "string", "InternalName of the target plugin", true),
                ("methodName", "string", "Registered IPC method name", true),
                ("value", "integer or string or boolean", "Optional argument for single-argument signatures", false)),
            args => CallEndpoint(svc, hub, args));

        registry.Add(
            "list_ipc_endpoints",
            "List IPC endpoints",
            "Lists every endpoint registered through register_ipc_endpoint, optionally filtered by plugin.",
            Json.Schema(
                ("pluginName", "string", "Optional: only endpoints of this plugin", false)),
            args => ListEndpoints(hub, args));

        registry.Add(
            "plugin_data_subscribe",
            "Subscribe to plugin data",
            "Retains the payloads a plugin pushes over the push channel under one key, in a " +
            "dedicated queue that plugin_data_poll drains incrementally. Payloads that arrive " +
            "while nothing is subscribed are only visible through query_push_data and are lost " +
            "to the rolling buffer once it wraps.",
            Json.Schema(
                ("key", "string", "Push-channel key (tag) to retain", true),
                ("capacity", "integer", "Retained entries before the oldest are dropped (16-10000, default 1000)", false)),
            args => SubscribeData(hub, args));

        registry.Add(
            "plugin_data_poll",
            "Poll subscribed plugin data",
            "Drains retained payloads for a subscribed key, oldest first. Drained entries are " +
            "removed, so each payload is delivered once; remaining entries stay for the next poll.",
            Json.Schema(
                ("key", "string", "Key passed to plugin_data_subscribe", true),
                ("maxItems", "integer", "Max entries to drain (1-10000, default 200)", false)),
            args => PollData(hub, args));

        registry.Add(
            "plugin_data_unsubscribe",
            "Unsubscribe from plugin data",
            "Stops retaining payloads for a key and discards anything still queued for it.",
            Json.Schema(
                ("key", "string", "Key passed to plugin_data_subscribe", true)),
            args => UnsubscribeData(hub, args));
    }

    private static JObject QueryPushData(IpcHub hub, JObject args)
    {
        var key = args.Value<string?>("key");
        var count = Math.Clamp(args.Value<int?>("count") ?? 50, 1, hub.PushData.MaxSize);
        var entries = hub.PushData.Query(key, count);
        return new JObject
        {
            ["total"] = hub.PushData.Count,
            ["entries"] = new JArray(entries.Select(e => new JObject
            {
                ["id"] = e.Id,
                ["key"] = e.Key,
                ["data"] = ParseOrRaw(e.JsonData),
                ["timestamp"] = e.Timestamp,
            })),
        };
    }

    private static JToken ParseOrRaw(string json)
    {
        try
        {
            return JToken.Parse(json);
        }
        catch
        {
            return json;
        }
    }

    private static JObject RegisterEndpoint(GameServices svc, IpcHub hub, JObject args)
    {
        var pluginName = args.Value<string>("pluginName")?.Trim() ?? string.Empty;
        var methodName = args.Value<string>("methodName")?.Trim() ?? string.Empty;
        var signature = args.Value<string>("signature") ?? string.Empty;
        var description = args.Value<string?>("description");

        IpcEndpoint endpoint;
        try
        {
            endpoint = hub.Endpoints.Register(pluginName, methodName, signature, description);
        }
        catch (ArgumentException ex)
        {
            throw new ToolException(ex.Message);
        }

        PersistEndpoints(svc, hub);
        return new JObject
        {
            ["success"] = true,
            ["endpoint"] = EndpointJson(endpoint),
        };
    }

    private static JObject CallEndpoint(GameServices svc, IpcHub hub, JObject args)
    {
        var pluginName = args.Value<string>("pluginName")?.Trim() ?? string.Empty;
        var methodName = args.Value<string>("methodName")?.Trim() ?? string.Empty;

        var endpoint = hub.Endpoints.Find(pluginName, methodName);
        if (endpoint is null)
            throw new ToolException($"{pluginName}.{methodName} is not registered; call register_ipc_endpoint first");

        var argumentJson = args.TryGetValue("value", out var valueTok)
            ? valueTok.ToString(Newtonsoft.Json.Formatting.None)
            : null;

        try
        {
            var result = PluginIpcInvoker.Invoke(svc.PluginInterface!, endpoint, argumentJson);
            return new JObject
            {
                ["success"] = true,
                ["pluginName"] = endpoint.PluginName,
                ["methodName"] = endpoint.MethodName,
                ["result"] = result is null ? JValue.CreateNull() : JToken.FromObject(result),
            };
        }
        catch (ArgumentException ex)
        {
            throw new ToolException(ex.Message);
        }
        catch (Exception ex)
        {
            throw new ToolException($"IPC call failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static JObject ListEndpoints(IpcHub hub, JObject args)
    {
        var pluginName = args.Value<string?>("pluginName");
        var endpoints = hub.Endpoints.List(pluginName);
        return new JObject
        {
            ["count"] = endpoints.Count,
            ["endpoints"] = new JArray(endpoints.Select(EndpointJson)),
        };
    }

    private static JObject SubscribeData(IpcHub hub, JObject args)
    {
        var key = args.Value<string>("key")?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
            throw new ToolException("key is required");

        var capacity = Math.Clamp(args.Value<int?>("capacity") ?? 1000, 16, 10000);
        var existed = hub.PushData.Find(key) is not null;
        var subscription = hub.PushData.Subscribe(key, capacity);
        return new JObject
        {
            ["success"] = true,
            ["status"] = existed ? "already_subscribed" : "subscribe_success",
            ["key"] = subscription.Key,
            ["capacity"] = subscription.Capacity,
            ["cursor"] = subscription.Cursor,
            ["received"] = subscription.Received,
            ["dropped"] = subscription.Dropped,
            ["pending"] = subscription.Pending.Count,
        };
    }

    private static JObject PollData(IpcHub hub, JObject args)
    {
        var key = args.Value<string>("key")?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
            throw new ToolException("key is required");

        var subscription = hub.PushData.Find(key);
        if (subscription is null)
            throw new ToolException($"'{key}' is not subscribed; call plugin_data_subscribe first");

        var maxItems = Math.Clamp(args.Value<int?>("maxItems") ?? 200, 1, 10000);
        var drained = hub.PushData.Poll(key, maxItems);
        return new JObject
        {
            ["success"] = true,
            ["status"] = drained.Count == 0 ? "no_data" : "data_available",
            ["key"] = key,
            ["count"] = drained.Count,
            ["cursor"] = subscription.Cursor,
            ["pending"] = subscription.Pending.Count,
            ["received"] = subscription.Received,
            ["dropped"] = subscription.Dropped,
            ["entries"] = new JArray(drained.Select(e => new JObject
            {
                ["id"] = e.Id,
                ["key"] = e.Key,
                ["data"] = ParseOrRaw(e.JsonData),
                ["timestamp"] = e.Timestamp,
            })),
        };
    }

    private static JObject UnsubscribeData(IpcHub hub, JObject args)
    {
        var key = args.Value<string>("key")?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(key))
            throw new ToolException("key is required");

        var removed = hub.PushData.Unsubscribe(key);
        return new JObject
        {
            ["success"] = removed,
            ["status"] = removed ? "unsubscribe_success" : "not_subscribed",
            ["key"] = key,
            ["remainingSubscriptions"] = hub.PushData.Subscriptions().Count,
        };
    }

    private static JObject EndpointJson(IpcEndpoint e) => new()
    {
        ["pluginName"] = e.PluginName,
        ["methodName"] = e.MethodName,
        ["signature"] = e.Signature,
        ["description"] = e.Description,
    };

    private static void PersistEndpoints(GameServices svc, IpcHub hub)
    {
        svc.Config.IpcEndpoints = hub.Endpoints.List()
            .Select(e => new IpcEndpointConfig
            {
                PluginName = e.PluginName,
                MethodName = e.MethodName,
                Signature = e.Signature,
                Description = e.Description,
            })
            .ToList();
        svc.PluginInterface!.SavePluginConfig(svc.Config);
    }
}
