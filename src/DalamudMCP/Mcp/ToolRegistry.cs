using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace DalamudMCP.Mcp;

/// <summary>One callable MCP tool: metadata, JSON schema, and its handler.</summary>
public sealed class McpTool
{
    public required string Name { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required JObject InputSchema { get; init; }

    /// <summary>True when the tool mutates game state and must be explicitly enabled in config.</summary>
    public bool Mutating { get; init; }

    /// <summary>Invoked with the request's <c>arguments</c> object. May return null (serialized as JSON null).</summary>
    public required Func<JObject, object?> Handler { get; init; }

    public JObject ToJson() => new()
    {
        ["name"] = Name,
        ["title"] = Title,
        ["description"] = Description,
        ["inputSchema"] = InputSchema,
        ["annotations"] = new JObject
        {
            ["title"] = Title,
            ["readOnlyHint"] = !Mutating,
            ["destructiveHint"] = Mutating,
            ["idempotentHint"] = !Mutating,
            ["openWorldHint"] = true,
        },
    };
}

/// <summary>Thrown by a tool handler to produce an MCP tool error (visible to the model).</summary>
public sealed class ToolException : Exception
{
    public ToolException(string message) : base(message) { }
}

/// <summary>The set of tools exposed by the MCP server.</summary>
public sealed class ToolRegistry
{
    private readonly List<McpTool> tools = new();
    private readonly Dictionary<string, McpTool> byName = new(StringComparer.Ordinal);

    /// <summary>Supplies whether mutating tools are currently permitted.</summary>
    public Func<bool> AllowMutating { get; set; } = () => false;

    public IReadOnlyList<McpTool> Tools => tools;

    public McpTool Add(McpTool tool)
    {
        if (!byName.TryAdd(tool.Name, tool))
            throw new InvalidOperationException($"duplicate tool name: {tool.Name}");
        tools.Add(tool);
        return tool;
    }

    public McpTool Add(
        string name,
        string title,
        string description,
        JObject schema,
        Func<JObject, object?> handler,
        bool mutating = false)
    {
        var args = schema["properties"] as JObject;
        if (args is not null)
            description = BuildDescription(description, args);

        return Add(new McpTool
        {
            Name = name,
            Title = title,
            Description = description,
            InputSchema = schema,
            Handler = handler,
            Mutating = mutating,
        });
    }

    private static string BuildDescription(string description, JObject args)
    {
        if (args.Count == 0) return description;

        var lines = new List<string>();
        foreach (var prop in args.Properties())
        {
            var type = prop.Value["type"]?.Value<string>() ?? "any";
            var desc = prop.Value["description"]?.Value<string>();
            var enumValues = prop.Value["enum"] is JArray e
                ? " (" + string.Join(" | ", e.Select(v => v.Value<string>())) + ")"
                : string.Empty;
            lines.Add($"  - {prop.Name} ({type}{enumValues}){(string.IsNullOrEmpty(desc) ? string.Empty : ": " + desc)}");
        }

        return description + "\n\nParameters:\n" + string.Join("\n", lines);
    }

    public McpTool? Find(string name) => byName.TryGetValue(name, out var t) ? t : null;

    public IEnumerable<McpTool> Visible()
    {
        var allow = AllowMutating();
        return tools.Where(t => !t.Mutating || allow);
    }
}
