using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Scheduler.Services;

/// <summary>
/// Read-only <see cref="IOrchestratorRunService"/> adapter for the Scheduler.
/// Implements only the methods called by <see cref="HousekeepingService"/> and
/// <see cref="OrphanedLabelRecoveryService"/> — all write methods throw <see cref="NotSupportedException"/>.
///
/// <b>Known limitations (Spec 047 deferred items):</b>
/// <list type="bullet">
///   <item><description>
///     <see cref="GetActiveRuns"/> always returns empty. The API's active-run endpoint does not
///     yet expose <c>BranchName</c>, so <see cref="HousekeepingService"/>'s stale-branch
///     exclusion guard is currently disabled. This is conservative (no false branch deletions).
///   </description></item>
///   <item><description>
///     <see cref="IsIssueBeingProcessed"/> always returns false for the same reason.
///     <see cref="OrphanedLabelRecoveryService"/> relies on Defense 1 (re-fetch current labels)
///     as its only active-run exclusion check.
///   </description></item>
///   <item><description>
///     <see cref="WasRecentlyCompleted"/> and <see cref="MarkRecentlyCompleted"/> use an
///     in-process cache. <c>MarkRecentlyCompleted</c> is only called by
///     <c>RunLifecycleManager</c> in the API process, so the Scheduler's cache is never
///     populated — Defense 2 of orphan recovery is inoperative in this process.
///   </description></item>
/// </list>
/// To fix all three: add <c>BranchName</c> to the active-run API response and expose
/// <c>IPipelineApiRunHistoryClient.GetActiveRunsAsync()</c>.
/// </summary>
public sealed class SchedulerRunQueryService : IOrchestratorRunService
{
    // Local in-memory recently-completed cache (IssueIdentifier:ProviderConfigId → expiry).
    // Note: populated only if MarkRecentlyCompleted is called in this process — currently
    // it is only called by RunLifecycleManager in the API, so this cache is always empty.
    private readonly Dictionary<string, DateTimeOffset> _recentlyCompleted = new();
    private const int RecentlyCompletedTtlSeconds = 120;
    private readonly Lock _lock = new();

    // ── Read methods used by HousekeepingService and OrphanedLabelRecoveryService ──

    /// <summary>
    /// Always returns empty until the API's active-run endpoint exposes BranchName.
    /// HousekeepingService conservatively skips branch deletion when no active branches
    /// are tracked — no false deletions occur.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetActiveRuns() => [];

    public bool HasActiveRuns => false; // GetActiveRuns() is always empty

    public int ActiveRunCount => 0; // GetActiveRuns() is always empty

    public PipelineRun? GetRun(RunId runId) => null;

    /// <summary>
    /// Always returns false because <see cref="GetActiveRuns"/> returns empty.
    /// OrphanedLabelRecoveryService falls through to Defense 1 (label re-fetch) as its
    /// only exclusion check.
    /// </summary>
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => false;

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

    // ── Write methods — not used by Scheduler ──

    public OutputRingBuffer GetOutputBuffer(RunId runId)
        => throw new NotSupportedException("SchedulerRunQueryService does not support output buffers.");

    public void AppendOutputLines(RunId runId, IReadOnlyList<string> lines)
        => throw new NotSupportedException("SchedulerRunQueryService does not support output writes.");

    public void AddRun(PipelineRun run)
        => throw new NotSupportedException("SchedulerRunQueryService is read-only.");

    public PipelineRun? RemoveRun(RunId runId)
        => throw new NotSupportedException("SchedulerRunQueryService is read-only.");

    public void ReplaceRun(PipelineRun run)
        => throw new NotSupportedException("SchedulerRunQueryService is read-only.");
}
