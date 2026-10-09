using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Read-only <see cref="IRunActivityQuery"/> adapter for the Scheduler.
/// Implements only the methods called by <see cref="HousekeepingService"/> and
/// <see cref="OrphanedLabelRecoveryService"/>.
///
/// <b>Known limitations (Spec 047 deferred items):</b>
/// <list type="bullet">
///   <item><description>
///     <see cref="IsIssueBeingProcessed"/> always returns false because in-memory run state is
///     unavailable in the Scheduler process.
///     <see cref="OrphanedLabelRecoveryService"/> uses Defense 3 (<see cref="IPipelineApiWorkItemClient.IsIssueDistributedAsync"/>)
///     as its primary active-run exclusion check, with Defense 1 (re-fetch current labels from GitHub)
///     as the terminal-label guard.
///   </description></item>
///   <item><description>
///     <see cref="WasRecentlyCompleted"/> and <see cref="MarkRecentlyCompleted"/> use an
///     in-process cache. <c>MarkRecentlyCompleted</c> is only called by
///     <c>RunLifecycleManager</c> in the API process, so the Scheduler's cache is never
///     populated — Defense 2 of orphan recovery is inoperative in this process.
///   </description></item>
/// </list>
/// </summary>
public sealed class SchedulerRunQueryService : IRunActivityQuery
{
    private readonly IPipelineApiRunHistoryClient _runHistoryClient;

    // Local in-memory recently-completed cache (IssueIdentifier:ProviderConfigId → expiry).
    // Note: populated only if MarkRecentlyCompleted is called in this process — currently
    // it is only called by RunLifecycleManager in the API, so this cache is always empty.
    private readonly Dictionary<string, DateTimeOffset> _recentlyCompleted = new();
    private const int RecentlyCompletedTtlSeconds = 120;
    private readonly Lock _lock = new();

    /// <summary>
    /// Constructs a <see cref="SchedulerRunQueryService"/> backed by the given API client.
    /// </summary>
    /// <param name="runHistoryClient">
    /// HTTP client for <c>/api/pipeline-runs</c>. Used by
    /// <see cref="GetActiveRunBranchesAsync"/> to fetch active-run branch names.
    /// </param>
    public SchedulerRunQueryService(IPipelineApiRunHistoryClient runHistoryClient)
    {
        ArgumentNullException.ThrowIfNull(runHistoryClient);
        _runHistoryClient = runHistoryClient;
    }

    // ── Read methods used by HousekeepingService and OrphanedLabelRecoveryService ──

    /// <summary>
    /// Always returns false because in-memory run state is unavailable in the Scheduler process.
    /// OrphanedLabelRecoveryService uses Defense 3 (IsIssueDistributedAsync API call)
    /// as its primary active-run guard.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="issueIdentifier"/>.Value is null or empty.
    /// </exception>
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        return false;
    }

    public bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        var key = $"{issueProviderConfigId.Value}:{issueIdentifier.Value}";
        lock (_lock)
        {
            if (_recentlyCompleted.TryGetValue(key, out var expiry))
            {
                if (DateTimeOffset.UtcNow <= expiry) return true;
                _recentlyCompleted.Remove(key);
            }
        }
        return false;
    }

    public void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        var key = $"{issueProviderConfigId.Value}:{issueIdentifier.Value}";
        lock (_lock)
        {
            _recentlyCompleted[key] = DateTimeOffset.UtcNow.AddSeconds(RecentlyCompletedTtlSeconds);
        }
    }

    /// <summary>
    /// Calls <c>GET /api/pipeline-runs/active-branches</c> to retrieve branch names of all
    /// active pipeline runs from the orchestrator process.
    /// Overrides the in-memory <c>GetActiveRuns</c>-based approach so the housekeeping
    /// branch-update guard works correctly in the Scheduler deployment.
    /// </summary>
    public async Task<HashSet<string>> GetActiveRunBranchesAsync(CancellationToken ct = default)
    {
        var branches = await _runHistoryClient.GetActiveBranchesAsync(ct);
        return branches.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }
}
