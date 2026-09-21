using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.OpenCode;

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
    /// Connects to the SSE stream (GET /event) and processes events for the given session.
    /// Routes events to the onOutputLine callback and auto-approves permission requests.
    /// Logs a warning on unexpected disconnect; does not reconnect.
    /// </summary>
    internal async Task ConnectAndProcessSseAsync(string sessionId, Action<string>? onOutputLine, CancellationToken ct,
        string? workspacePath = null, Action<bool>? onSseEmitted = null)
    {
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

                var sseEvent = TryParseSseLine(line);
                if (sseEvent is null || sseEvent.SessionId != sessionId)
                    continue;

                await ProcessSseEventAsync(sseEvent, sessionId, onOutputLine, onSseEmitted, workspacePath, ct);
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
    /// Routes a single SSE event to the appropriate handler based on its type.
    /// Updates LastOutputTime only for events that represent meaningful agent progress.
    /// </summary>
    private async Task ProcessSseEventAsync(
        SseEvent sseEvent,
        string sessionId,
        Action<string>? onOutputLine,
        Action<bool>? onSseEmitted,
        string? workspacePath,
        CancellationToken ct)
    {
        switch (sseEvent.Type)
        {
            case "message.part.updated":
                LastOutputTime = DateTime.UtcNow;
                onSseEmitted?.Invoke(true);
                onOutputLine?.Invoke(StripAnsiEscapes($"[assistant] {sseEvent.Part?.Text}"));
                break;

            case "tool.execute.before":
                LastOutputTime = DateTime.UtcNow;
                onOutputLine?.Invoke(StripAnsiEscapes($"[tool_call] {sseEvent.ToolName} {sseEvent.ToolArgs}"));
                break;

            case "tool.execute.after":
                LastOutputTime = DateTime.UtcNow;
                onOutputLine?.Invoke(StripAnsiEscapes($"[tool_result] {sseEvent.ToolResult}"));
                break;

            case "permission.updated":
                LastOutputTime = DateTime.UtcNow;
                await AutoApprovePermissionAsync(sessionId, sseEvent.PermissionId, ct, workspacePath);
                break;

            case "session.idle":
                // Signal completion — informational only, sync message response is primary
                _sessionStatus = "idle";
                _sessionStatusMessage = null;
                break;

            case "session.status":
                HandleSessionStatusEvent(sseEvent, sessionId, onOutputLine);
                break;

            default:
                // Discard metadata events (session.updated, session.diff, message.updated, etc.)
                break;
        }
    }

    /// <summary>
    /// Handles session.status SSE events by updating session status fields and
    /// logging/emitting retry details when the provider indicates a retry.
    /// </summary>
    private void HandleSessionStatusEvent(SseEvent sseEvent, string sessionId, Action<string>? onOutputLine)
    {
        if (sseEvent.Status is null)
            return;

        _sessionStatus = sseEvent.Status.Type;
        if (string.Equals(sseEvent.Status.Type, "retry", StringComparison.OrdinalIgnoreCase))
        {
            var retryMsg = sseEvent.Status.Message ?? "unknown error";
            var provider = sseEvent.Status.Action?.Provider;
            _sessionStatusMessage = provider is not null
                ? $"[{provider}] attempt {sseEvent.Status.Attempt}: {retryMsg}"
                : $"attempt {sseEvent.Status.Attempt}: {retryMsg}";
            _logger.Warning("Session {SessionId} retry status: {Message}", sessionId, _sessionStatusMessage);
            onOutputLine?.Invoke(StripAnsiEscapes($"[session.status] retry — {_sessionStatusMessage}"));
        }
        else
        {
            _sessionStatusMessage = null;
        }
    }

    /// <summary>
    /// Auto-approves a permission request by calling POST /session/:id/permissions/:permissionId.
    /// Best-effort — logs warning on failure without rethrowing.
    /// </summary>
    private async Task AutoApprovePermissionAsync(string sessionId, string? permissionId, CancellationToken ct, string? workspacePath = null)
    {
        if (string.IsNullOrEmpty(permissionId))
            return;

        try
        {
            _logger.Debug("POST /session/{SessionId}/permissions/{PermissionId} (auto-approve)", sessionId, permissionId);
            using var client = workspacePath is not null
                ? CreateDirectoryClientForPath(workspacePath)
                : CreateDirectoryClient();
            var body = new PermissionResponse { Response = "allow", Remember = true };
            await client.PostAsJsonAsync($"/session/{sessionId}/permissions/{permissionId}", body, OpenCodeJson.JsonOptions, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to auto-approve permission {PermissionId} for session {SessionId}", permissionId, sessionId);
        }
    }
}
