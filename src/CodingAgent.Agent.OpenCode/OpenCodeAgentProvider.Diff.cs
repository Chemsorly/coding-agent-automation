using System.Net.Http.Json;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.OpenCode;

public sealed partial class OpenCodeAgentProvider
{
    // ── IOpenCodeDiffProvider ───────────────────────────────────────────

    public async Task<IReadOnlyList<FileChangeSummary>> GetSessionDiffAsync(CancellationToken ct)
    {
        var sessionId = _lastKnownSessionId;
        if (sessionId is null)
            return Array.Empty<FileChangeSummary>();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

            using var client = CreateDirectoryClient();
            var response = await client.GetAsync($"/session/{sessionId}/diff", timeoutCts.Token);
            response.EnsureSuccessStatusCode();

            var diffs = await response.Content.ReadFromJsonAsync<FileDiff[]>(OpenCodeJson.JsonOptions, timeoutCts.Token);
            if (diffs is null || diffs.Length == 0)
                return Array.Empty<FileChangeSummary>();

            var results = new List<FileChangeSummary>(diffs.Length);
            foreach (var fileDiff in diffs)
            {
                var status = MapDiffStatus(fileDiff.Status);
                results.Add(new FileChangeSummary(status, fileDiff.Path, fileDiff.LinesAdded, fileDiff.LinesDeleted));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to retrieve diff for session {SessionId}", sessionId);
            return Array.Empty<FileChangeSummary>();
        }
    }

    private static string MapDiffStatus(string? status)
    {
        if (string.Equals(status, "added", StringComparison.OrdinalIgnoreCase))
            return "Added";
        if (string.Equals(status, "deleted", StringComparison.OrdinalIgnoreCase))
            return "Deleted";
        return "Modified";
    }
}
