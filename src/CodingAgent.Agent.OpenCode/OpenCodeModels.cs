using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodingAgent.Agent.OpenCode;

/// <summary>
/// JSON serialization options for OpenCode API payloads.
/// Uses camelCase naming to match the OpenCode REST API convention; IDs use the API's
/// <c>sessionID</c>/<c>messageID</c> spelling through <see cref="JsonPropertyNameAttribute"/>.
/// </summary>
internal static class OpenCodeJson
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

/// <summary>Request body for POST /session.</summary>
public sealed record CreateSessionRequest
{
    public required string Title { get; init; }

    /// <summary>Working directory for the session. OpenCode uses this as the project root.</summary>
    public string? Path { get; init; }
}

/// <summary>Response from POST /session.</summary>
public sealed record CreateSessionResponse
{
    public required string Id { get; init; }
}

/// <summary>A single part in a message request or response.</summary>
public sealed record MessagePart
{
    /// <summary>Part ID; set on response parts, left out of request parts.</summary>
    public string? Id { get; init; }

    public required string Type { get; init; }
    public string? Text { get; init; }
    public string? Mime { get; init; }
    public string? Url { get; init; }
    public string? Filename { get; init; }
}

/// <summary>A model as OpenCode's prompt API takes it: <c>{"providerID": "...", "modelID": "..."}</c>.</summary>
public sealed record OpenCodeModelRef
{
    [JsonPropertyName("providerID")] public required string ProviderId { get; init; }
    [JsonPropertyName("modelID")] public required string ModelId { get; init; }

    /// <summary>
    /// Parses the configured <c>provider/model</c> value; null for a blank, <c>auto</c> or
    /// provider-less value, which leaves the choice to the server's configuration.
    /// </summary>
    public static OpenCodeModelRef? Parse(string? model)
    {
        if (string.IsNullOrWhiteSpace(model) || model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        var slash = model.IndexOf('/');
        return slash > 0 && slash < model.Length - 1
            ? new OpenCodeModelRef { ProviderId = model[..slash].Trim(), ModelId = model[(slash + 1)..].Trim() }
            : null;
    }
}

/// <summary>Request body for POST /session/:id/message.</summary>
public sealed record SendMessageRequest
{
    public required IReadOnlyList<MessagePart> Parts { get; init; }

    /// <summary>The model to run; null uses the server's configured default.</summary>
    public OpenCodeModelRef? Model { get; init; }
}

/// <summary>Response from POST /session/:id/message: the assistant message and its parts.</summary>
public sealed record SendMessageResponse
{
    public SendMessageInfo? Info { get; init; }
    public IReadOnlyList<MessagePart> Parts { get; init; } = [];
}

/// <summary>The assistant message of a prompt response. A failed turn still answers HTTP 200, with <see cref="Error"/> set.</summary>
public sealed record SendMessageInfo
{
    public string? Id { get; init; }
    public OpenCodeError? Error { get; init; }
}

/// <summary>An OpenCode error object, e.g. <c>{"name": "APIError", "data": {"message": "...", "statusCode": 429}}</c>.</summary>
public sealed record OpenCodeError
{
    public string? Name { get; init; }
    public OpenCodeErrorData? Data { get; init; }
}

public sealed record OpenCodeErrorData
{
    public string? Message { get; init; }
    public int? StatusCode { get; init; }
}

/// <summary>Request body for POST /permission/:requestID/reply.</summary>
public sealed record PermissionReply
{
    /// <summary><c>once</c>, <c>always</c> or <c>reject</c>.</summary>
    public required string Reply { get; init; }
}

/// <summary>Response from GET /global/health.</summary>
public sealed record HealthResponse
{
    public required bool Healthy { get; init; }
    public string? Version { get; init; }
}

/// <summary>
/// An event from GET /event: <c>{"id": "...", "type": "...", "properties": {...}}</c>.
/// </summary>
public sealed record SseEvent
{
    public required string Type { get; init; }
    public SseEventProperties Properties { get; init; } = new();
}

/// <summary>The fields of <see cref="SseEvent.Properties"/> the provider reads; each event type sets some.</summary>
public sealed record SseEventProperties
{
    [JsonPropertyName("sessionID")] public string? SessionId { get; init; }

    /// <summary>Request ID of <c>permission.asked</c> and <c>question.asked</c>.</summary>
    public string? Id { get; init; }

    /// <summary>The part of <c>message.part.updated</c>.</summary>
    public SsePart? Part { get; init; }

    /// <summary>The message of <c>message.updated</c>.</summary>
    public SseMessageInfo? Info { get; init; }

    /// <summary>Session status payload (present on "session.status" events).</summary>
    public SseSessionStatus? Status { get; init; }
}

/// <summary>A message part as <c>message.part.updated</c> carries it.</summary>
public sealed record SsePart
{
    public string? Id { get; init; }
    [JsonPropertyName("messageID")] public string? MessageId { get; init; }
    public string? Type { get; init; }
    public string? Text { get; init; }
    public SsePartTime? Time { get; init; }

    /// <summary>Tool name of a <c>tool</c> part.</summary>
    public string? Tool { get; init; }

    [JsonPropertyName("callID")] public string? CallId { get; init; }
    public SseToolState? State { get; init; }
}

public sealed record SsePartTime
{
    public long? Start { get; init; }

    /// <summary>Set once a text part is complete.</summary>
    public long? End { get; init; }
}

/// <summary>State of a tool part: <c>pending</c>, <c>running</c>, <c>completed</c> or <c>error</c>.</summary>
public sealed record SseToolState
{
    public string? Status { get; init; }
    public string? Title { get; init; }
    public string? Error { get; init; }
}

/// <summary>The message of a <c>message.updated</c> event.</summary>
public sealed record SseMessageInfo
{
    public string? Id { get; init; }
    public string? Role { get; init; }
}

/// <summary>
/// Session status payload from the "session.status" SSE event.
/// Maps to OpenCode's SessionStatus schema: idle | busy | retry.
/// </summary>
public sealed record SseSessionStatus
{
    /// <summary>Status type: "idle", "busy", or "retry".</summary>
    public required string Type { get; init; }

    /// <summary>Retry attempt number (only present when Type == "retry").</summary>
    public int? Attempt { get; init; }

    /// <summary>Error message describing why the retry is occurring (only present when Type == "retry").</summary>
    public string? Message { get; init; }

    /// <summary>Unix timestamp (seconds) for when the next retry will occur (only present when Type == "retry").</summary>
    public long? Next { get; init; }

    /// <summary>Action details when OpenCode surfaces provider-specific context.</summary>
    public SseSessionStatusAction? Action { get; init; }
}

/// <summary>Provider-specific action context from a retry status event.</summary>
public sealed record SseSessionStatusAction
{
    public string? Reason { get; init; }
    public string? Provider { get; init; }
    public string? Title { get; init; }
    public string? Message { get; init; }
    public string? Label { get; init; }
    public string? Link { get; init; }
}

/// <summary>Token usage from GET /session/:id. Tracks input/output/reasoning tokens and cache hits.</summary>
public sealed record SessionTokenUsage
{
    public long Input { get; init; }
    public long Output { get; init; }
    public long Reasoning { get; init; }
    public SessionCacheUsage? Cache { get; init; }
}

/// <summary>Cache token usage (prompt caching).</summary>
public sealed record SessionCacheUsage
{
    public long Read { get; init; }
    public long Write { get; init; }
}

/// <summary>Response from GET /session/:id with token usage and cost.</summary>
public sealed record SessionDetailResponse
{
    public required string Id { get; init; }
    public double Cost { get; init; }
    public SessionTokenUsage? Tokens { get; init; }
}
