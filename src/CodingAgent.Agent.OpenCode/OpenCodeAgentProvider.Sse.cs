using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;

namespace CodingAgent.Agent.OpenCode;

/// <summary>
/// What one call's SSE reader has seen, shared with the response handler so text the stream
/// already showed is not shown again from the HTTP response.
/// </summary>
internal sealed class SseCallState
{
    /// <summary>IDs of the call session's assistant messages (from <c>message.updated</c>).</summary>
    public ConcurrentDictionary<string, byte> AssistantMessageIds { get; } = new(StringComparer.Ordinal);

    /// <summary>IDs of the text parts already emitted.</summary>
    public ConcurrentDictionary<string, byte> EmittedTextPartIds { get; } = new(StringComparer.Ordinal);

    /// <summary>Tool calls already announced.</summary>
    public ConcurrentDictionary<string, byte> AnnouncedToolCalls { get; } = new(StringComparer.Ordinal);
}

public sealed partial class OpenCodeAgentProvider
{
    /// <summary>
    /// Tears down the SSE reader by waiting briefly for late-arriving events,
    /// then cancelling and awaiting the reader task.
    /// </summary>
    private static async Task TearDownSseAsync(CancellationTokenSource sseCts, Task sseTask)
    {
        // Allow a brief window for late-arriving SSE events (e.g., final
        // message.part.updated) to be processed before tearing down the stream.
        try { await Task.Delay(500, CancellationToken.None); } catch { /* intentional: delay is best-effort; any exception (e.g. TaskCanceledException) is safely ignored here */ }
        await sseCts.CancelAsync();
        try { await sseTask.ConfigureAwait(false); } catch { /* expected cancellation */ }
        sseCts.Dispose();
    }

    /// <summary>
    /// Connects to the SSE stream (GET /event) and processes events for the given session: emits
    /// the assistant's completed text parts and tool steps, keeps the stall clock running, answers
    /// permission requests and rejects questions (no one can answer them). Logs a warning on an
    /// unexpected disconnect; does not reconnect.
    /// </summary>
    internal async Task ConnectAndProcessSseAsync(string sessionId, Action<string>? onOutputLine, CancellationToken ct,
        string? workspacePath = null, SseCallState? state = null)
    {
        state ??= new SseCallState();
        using var client = workspacePath is not null
            ? CreateDirectoryClientForPath(workspacePath)
            : CreateDirectoryClient();

        try
        {
            // 5-second connection timeout — only applies to establishing the connection
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectCts.CancelAfter(TimeSpan.FromSeconds(5));

            _logger.Debug("GET /event (SSE stream for session {SessionId})", sessionId);

            using var request = new HttpRequestMessage(HttpMethod.Get, "/event");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, connectCts.Token);
            response.EnsureSuccessStatusCode();

            using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);

            // After connection is established, use the original cancellation token (not the 5s timeout)
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    break; // stream closed by server

                if (TryParseSseLine(line) is { } sseEvent)
                    await ProcessSseEventAsync(sseEvent, sessionId, onOutputLine, state, workspacePath, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on completion or caller cancellation — just return
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "SSE stream disconnected unexpectedly");
        }
    }

    /// <summary>
    /// Parses a raw SSE stream line into an <see cref="SseEvent"/>. Returns null if the line
    /// is not a data line, is empty, or cannot be deserialized.
    /// </summary>
    private static SseEvent? TryParseSseLine(string line)
    {
        if (!line.StartsWith("data:", StringComparison.Ordinal))
            return null;
        var json = line["data:".Length..].Trim();
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<SseEvent>(json, OpenCodeJson.JsonOptions);
        }
        catch (JsonException)
        {
            return null; // Malformed SSE data line — skip
        }
    }

    /// <summary>
    /// Routes a single SSE event. Permission requests and questions are answered whatever session
    /// asks: a subagent runs in a child session of the call's, and nothing else uses this server.
    /// Everything else is filtered to the call's session.
    /// </summary>
    private async Task ProcessSseEventAsync(
        SseEvent sseEvent,
        string sessionId,
        Action<string>? onOutputLine,
        SseCallState state,
        string? workspacePath,
        CancellationToken ct)
    {
        var properties = sseEvent.Properties;
        switch (sseEvent.Type)
        {
            case "permission.asked":
                await ReplyAsync($"/permission/{properties.Id}/reply", new PermissionReply { Reply = "always" }, properties.Id, workspacePath, ct);
                return;

            case "question.asked":
                await ReplyAsync<object?>($"/question/{properties.Id}/reject", null, properties.Id, workspacePath, ct);
                return;
        }

        if (properties.SessionId != sessionId)
            return;

        switch (sseEvent.Type)
        {
            case "message.updated":
                if (properties.Info is { Role: "assistant", Id: { } messageId })
                    state.AssistantMessageIds.TryAdd(messageId, 0);
                break;

            case "message.part.delta":
                LastOutputTime = DateTime.UtcNow; // streaming tokens: the agent is working
                break;

            case "message.part.updated":
                LastOutputTime = DateTime.UtcNow;
                if (properties.Part is { } part)
                    EmitPart(part, state, onOutputLine);
                break;

            case "session.idle":
                // Signal completion — informational only, sync message response is primary
                _sessionStatus = "idle";
                _sessionStatusMessage = null;
                break;

            case "session.status":
                HandleSessionStatusEvent(properties.Status, sessionId, onOutputLine);
                break;

            default:
                // Discard metadata events (session.updated, session.diff, ...)
                break;
        }
    }

    /// <summary>
    /// Emits an assistant text part once it is complete, and a tool step once per call (plus its
    /// error). The user's own prompt parts arrive here too and are skipped.
    /// </summary>
    private static void EmitPart(SsePart part, SseCallState state, Action<string>? onOutputLine)
    {
        if (onOutputLine is null)
            return;

        switch (part.Type)
        {
            case "text" when part.Time?.End is not null
                && part.MessageId is { } messageId && state.AssistantMessageIds.ContainsKey(messageId)
                && part.Id is { } partId && state.EmittedTextPartIds.TryAdd(partId, 0):
                foreach (var line in (part.Text ?? "").ReplaceLineEndings("\n").Split('\n'))
                    onOutputLine(StripAnsiEscapes(line));
                break;

            case "tool" when part.State is { } toolState:
                var callId = part.CallId ?? part.Id ?? "";
                if (toolState.Status is "running" or "completed" && state.AnnouncedToolCalls.TryAdd(callId, 0))
                    onOutputLine(StripAnsiEscapes(string.IsNullOrEmpty(toolState.Title) ? $"▶ {part.Tool}" : $"▶ {part.Tool}: {toolState.Title}"));
                else if (toolState.Status == "error" && state.AnnouncedToolCalls.TryAdd(callId + ":error", 0))
                    onOutputLine(StripAnsiEscapes($"✖ {part.Tool}: {toolState.Error}"));
                break;
        }
    }

    /// <summary>
    /// Handles session.status SSE events by updating session status fields and
    /// logging/emitting retry details when the provider indicates a retry.
    /// </summary>
    private void HandleSessionStatusEvent(SseSessionStatus? status, string sessionId, Action<string>? onOutputLine)
    {
        if (status is null)
            return;

        _sessionStatus = status.Type;
        if (string.Equals(status.Type, "retry", StringComparison.OrdinalIgnoreCase))
        {
            var retryMsg = status.Message ?? "unknown error";
            var provider = status.Action?.Provider;
            _sessionStatusMessage = provider is not null
                ? $"[{provider}] attempt {status.Attempt}: {retryMsg}"
                : $"attempt {status.Attempt}: {retryMsg}";
            _logger.Warning("Session {SessionId} retry status: {Message}", sessionId, _sessionStatusMessage);
            onOutputLine?.Invoke(StripAnsiEscapes($"[session.status] retry — {_sessionStatusMessage}"));
        }
        else
        {
            _sessionStatusMessage = null;
        }
    }

    /// <summary>
    /// Answers a permission request or question. Best-effort — logs a warning on failure. Parallel
    /// calls in one workspace all see the request, so all but the first answer get a 404.
    /// </summary>
    private async Task ReplyAsync<T>(string path, T body, string? requestId, string? workspacePath, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(requestId))
            return;

        try
        {
            _logger.Debug("POST {Path} (auto-answer)", path);
            using var client = workspacePath is not null
                ? CreateDirectoryClientForPath(workspacePath)
                : CreateDirectoryClient();
            using var response = body is null
                ? await client.PostAsync(path, null, ct)
                : await client.PostAsJsonAsync(path, body, OpenCodeJson.JsonOptions, ct);
            if (!response.IsSuccessStatusCode)
                _logger.Debug("Auto-answer {Path} returned HTTP {Status}", path, (int)response.StatusCode);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to answer OpenCode request {RequestId}", requestId);
        }
    }
}
