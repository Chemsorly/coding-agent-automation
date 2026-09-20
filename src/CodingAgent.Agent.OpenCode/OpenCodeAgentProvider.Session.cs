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
            // Cache the session for this workspace so future UseResume=true calls reuse it
            _sessionByWorkspace[workspacePath] = sessionId;
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
    private async Task PollAllSessionStatusesAsync(CancellationToken ct)
    {
        // Small initial delay to let the session start
        try { await Task.Delay(2000, ct); } catch (OperationCanceledException) { return; }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TryRefreshAllSessionStatusSummaryAsync(ct);
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

    private async Task TryRefreshAllSessionStatusSummaryAsync(CancellationToken ct)
    {
        // GET /session/status returns all sessions globally — no directory header needed.
        using var client = CreateDirectoryClient();
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
