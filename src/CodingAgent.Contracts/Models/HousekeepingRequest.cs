using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Inputs for one <see cref="IHousekeepingService.ExecuteAsync"/> pass over a single template's repository.
/// </summary>
public sealed record HousekeepingRequest
{
    /// <summary>Provider to call for mergeability checks and updates.</summary>
    public required IRepositoryProvider RepoProvider { get; init; }

    /// <summary>Identifier for in-flight tracking scope (per repository).</summary>
    public required string RepoProviderId { get; init; }

    /// <summary>Provider for fetching issue details and swapping labels.</summary>
    public required IIssueProvider IssueProvider { get; init; }

    /// <summary>Issue provider config ID for label swap routing.</summary>
    public required string IssueProviderId { get; init; }

    /// <summary>Current agent:done PR list for this template. May be empty.</summary>
    public required IReadOnlyList<PullRequestSummary> AgentDonePrs { get; init; }

    /// <summary>
    /// True when <see cref="AgentDonePrs"/> was capped by <c>ClosedLoopMaxPagesToFetch</c>.
    /// Forwarded to <see cref="IStaleBranchCleaner"/> — when true, branch cleanup is skipped
    /// with a Warning rather than risking deleting a branch whose PR was beyond the cap.
    /// </summary>
    public required bool WasInputTruncated { get; init; }

    /// <summary>Max in-flight updates for this repo. Clamped to ≥ 1.</summary>
    public required int EffectiveConcurrencyLimit { get; init; }

    /// <summary>Whether to run stale branch cleanup this cycle.</summary>
    public required bool BranchCleanupEnabled { get; init; }

    /// <summary>Minimum minutes between cleanup passes. 0 = every tick.</summary>
    public required int CleanupIntervalMinutes { get; init; }

    /// <summary>
    /// Minimum minutes between consecutive branch-update triggers for the same PR. Clamped to ≥ 1.
    /// Sourced from <see cref="PipelineConfiguration.HousekeepingTriggerCooldownMinutes"/>.
    /// </summary>
    public required int TriggerCooldownMinutes { get; init; }
}
