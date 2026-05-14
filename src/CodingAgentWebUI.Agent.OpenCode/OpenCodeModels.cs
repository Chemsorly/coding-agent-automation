using System.Text.Json;

namespace CodingAgentWebUI.Agent.OpenCode;

/// <summary>
/// JSON serialization options for OpenCode API payloads.
/// Uses camelCase naming to match the OpenCode REST API convention.
/// </summary>
internal static class OpenCodeJson
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
}

/// <summary>Request body for POST /session.</summary>
public sealed record CreateSessionRequest
{
    public required string Title { get; init; }
}

/// <summary>Response from POST /session.</summary>
public sealed record CreateSessionResponse
{
    public required string Id { get; init; }
}

/// <summary>A single part in a message request or response.</summary>
public sealed record MessagePart
{
    public required string Type { get; init; }
    public string? Text { get; init; }
}

/// <summary>Request body for POST /session/:id/message.</summary>
public sealed record SendMessageRequest
{
    public required IReadOnlyList<MessagePart> Parts { get; init; }

    /// <summary>Model in "provider/model" format (e.g., "anthropic/claude-sonnet-4-20250514").</summary>
    public string? Model { get; init; }
}

/// <summary>Response from POST /session/:id/message.</summary>
public sealed record SendMessageResponse
{
    public IReadOnlyList<MessagePart> Parts { get; init; } = [];
}

/// <summary>Request body for POST /session/:id/permissions/:permissionId.</summary>
public sealed record PermissionResponse
{
    public required string Response { get; init; }
    public required bool Remember { get; init; }
}

/// <summary>Request body for POST /mcp.</summary>
public sealed record RegisterMcpRequest
{
    public required string Name { get; init; }
    public required object Config { get; init; }
}

/// <summary>MCP config for stdio-type servers.</summary>
public sealed record McpStdioConfig
{
    public required string Command { get; init; }
    public IReadOnlyList<string> Args { get; init; } = [];
    public IReadOnlyDictionary<string, string> Env { get; init; } = new Dictionary<string, string>();
}

/// <summary>MCP config for HTTP-type servers.</summary>
public sealed record McpHttpConfig
{
    public required string Url { get; init; }
}

/// <summary>Response from GET /global/health.</summary>
public sealed record HealthResponse
{
    public required bool Healthy { get; init; }
    public string? Version { get; init; }
}

/// <summary>A single file diff entry from GET /session/:id/diff.</summary>
public sealed record FileDiff
{
    public required string Path { get; init; }
    public string? Status { get; init; }
    public int LinesAdded { get; init; }
    public int LinesDeleted { get; init; }
}

/// <summary>SSE event payload structure.</summary>
public sealed record SseEvent
{
    public required string Type { get; init; }
    public string? SessionId { get; init; }
    public string? PermissionId { get; init; }
    public MessagePart? Part { get; init; }
    public string? ToolName { get; init; }
    public string? ToolArgs { get; init; }
    public string? ToolResult { get; init; }
}
