using System.Net.Http.Json;
using System.Text.Json;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.OpenCode;

public sealed partial class OpenCodeAgentProvider
{
    public Task EnsureSessionAsync(WorkspacePath workspacePath, CancellationToken ct)
    {
        // No-op: sessions are now created per-ExecuteAsync call based on the workspace path.
        // The opencode server manages session lifecycle internally.
        return Task.CompletedTask;
    }

    private async Task<string?> ResolveSessionIdAsync(AgentRequest request, CancellationToken ct)
    {
        // ResumeSessionId takes precedence (explicit session targeting, e.g., adversarial review refinement)
        if (!string.IsNullOrEmpty(request.ResumeSessionId))
        {
            return request.ResumeSessionId;
        }

        var workspacePath = Path.GetFullPath(request.WorkspacePath);

        // UseResume=true within the same workspace → reuse the cached session for that workspace
        if (request.UseResume && _sessionByWorkspace.TryGetValue(workspacePath, out var cachedSessionId))
        {
            _logger.Debug("Reusing cached session {SessionId} for workspace {WorkspacePath}",
                cachedSessionId, workspacePath);
            return cachedSessionId;
        }

        // Create a fresh session for this workspace (UseResume=false, or no cached session yet)
        var sessionId = await CreateIsolatedSessionAsync(request.WorkspacePath, ct);
        if (sessionId is not null)
        {
            // The first session in a workspace is its main conversation, which UseResume=true calls
            // reuse. Later fresh sessions are isolated calls (reviewers) and must not replace it.
            _sessionByWorkspace.TryAdd(workspacePath, sessionId);
            _lastKnownSessionId = sessionId;
        }
        return sessionId;
    }

    /// <summary>
    /// Creates a new session and returns the ID without writing to shared instance fields.
    /// Used for isolated (non-resume) calls to enable safe parallel execution.
    /// </summary>
    private async Task<string?> CreateIsolatedSessionAsync(string workspacePath, CancellationToken ct)
    {
        try
        {
            var absolutePath = Path.GetFullPath(workspacePath);
            var title = Path.GetFileName(absolutePath) ?? absolutePath;

            using var client = CreateDirectoryClientForPath(absolutePath);
            var request = new CreateSessionRequest { Title = title, Path = absolutePath };

            var response = await client.PostAsJsonAsync("/session", request, OpenCodeJson.JsonOptions, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<CreateSessionResponse>(OpenCodeJson.JsonOptions, ct);
            if (result is not null)
            {
                _logger.Debug("Created isolated session {SessionId} for workspace {WorkspacePath}",
                    result.Id, absolutePath);
                return result.Id;
            }

            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Real cancellation — propagate
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to create isolated session for workspace {WorkspacePath}", workspacePath);
            return null;
        }
    }

    /// <summary>
    /// Background loop that polls GET /session/status every 10s and caches a human-readable
    /// summary of all session statuses (including child/subagent sessions). This provides
    /// observability into subagent retries that don't surface on the parent session's SSE stream.
    /// </summary>
    private Task PollAllSessionStatusesAsync(string workspacePath, CancellationToken ct) =>
        PollAllSessionStatusesAsync(workspacePath, ct, initialDelayMs: 2000);

    private async Task PollAllSessionStatusesAsync(string? workspacePath, CancellationToken ct, int initialDelayMs)
    {
        // Small initial delay to let the session start
        try { await Task.Delay(initialDelayMs, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TryRefreshAllSessionStatusSummaryAsync(workspacePath, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
#pragma warning disable S108 // Intentional: diagnostic polling is best-effort; failures must not affect agent execution.
            catch
            {
                // Intentional: diagnostic polling is best-effort; failures must not affect agent execution.
            }
#pragma warning restore S108

            try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { break; }
        }
    }

    // Test seams — accessible to CodingAgent.Agent.UnitTests via InternalsVisibleTo

    /// <summary>Exposes TryRefreshAllSessionStatusSummaryAsync for unit testing.</summary>
    internal Task TryRefreshAllSessionStatusSummaryAsyncForTest(CancellationToken ct, string? workspacePath = null) =>
        TryRefreshAllSessionStatusSummaryAsync(workspacePath, ct);

    /// <summary>Exposes PollAllSessionStatusesAsync for unit testing with a configurable initial delay.</summary>
    internal Task PollAllSessionStatusesAsyncForTest(CancellationToken ct, int initialDelayMs = 2000, string? workspacePath = null) =>
        PollAllSessionStatusesAsync(workspacePath, ct, initialDelayMs);

    /// <summary>Exposes _allSessionsSummary for unit testing.</summary>
    internal string? AllSessionsSummaryForTest => _allSessionsSummary;

    private async Task TryRefreshAllSessionStatusSummaryAsync(string? workspacePath, CancellationToken ct)
    {
        // GET /session/status is per directory instance: without the header it reports the server's
        // own working directory, not the workspace's sessions.
        using var client = workspacePath is not null ? CreateDirectoryClientForPath(workspacePath) : CreateDirectoryClient();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        var response = await client.GetAsync("/session/status", timeoutCts.Token);
        if (!response.IsSuccessStatusCode) return;

        var json = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        var statuses = JsonSerializer.Deserialize<Dictionary<string, SseSessionStatus>>(json, OpenCodeJson.JsonOptions);

        _allSessionsSummary = statuses is { Count: > 0 }
            ? BuildSessionStatusSummary(statuses)
            : null;
    }

    private static string BuildSessionStatusSummary(Dictionary<string, SseSessionStatus> statuses)
    {
        var retryCount = 0; var busyCount = 0; var idleCount = 0;
        foreach (var (_, status) in statuses)
        {
            if (string.Equals(status.Type, "retry", StringComparison.OrdinalIgnoreCase)) retryCount++;
            else if (string.Equals(status.Type, "busy", StringComparison.OrdinalIgnoreCase)) busyCount++;
            else idleCount++;
        }

        var parts = new List<string> { $"{statuses.Count} total" };
        if (retryCount > 0) parts.Add($"{retryCount} retrying");
        if (busyCount > 0) parts.Add($"{busyCount} busy");
        if (idleCount > 0) parts.Add($"{idleCount} idle");

        var retryDetails = statuses
            .Where(kv => string.Equals(kv.Value.Type, "retry", StringComparison.OrdinalIgnoreCase))
            .Take(3)
            .Select(kv => $"attempt {kv.Value.Attempt}: {kv.Value.Message ?? "unknown"}")
            .ToList();
        if (retryDetails.Count > 0)
            parts.Add($"detail: {string.Join("; ", retryDetails)}");

        return string.Join(", ", parts);
    }
}
