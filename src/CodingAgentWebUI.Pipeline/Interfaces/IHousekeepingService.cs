using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Evaluates agent:done PRs, evicts resolved in-flight entries, and triggers
/// server-side branch updates for PRs that are behind base within the concurrency budget.
/// </summary>
/// <remarks>
/// The service is stateful: it holds an in-flight set per repository across poll ticks
/// to enforce the concurrency limit over the CI run lifetime (not just the HTTP call lifetime).
/// Individual update calls are fire-and-forget — the method returns as soon as eligible
/// PRs have been dispatched, not when their CI runs complete.
///
/// Must be called even when <paramref name="agentDonePrs"/> is empty so that the eviction
/// pass can free slots for PRs that have since merged.
/// </remarks>
public interface IHousekeepingService
{
    /// <summary>
    /// Evicts resolved in-flight entries, then triggers server-side branch updates
    /// for eligible PRs within the concurrency budget.
    /// </summary>
    /// <param name="repoProvider">Provider to call for mergeability checks and updates.</param>
    /// <param name="repoProviderId">Identifier for in-flight tracking scope (per repository).</param>
    /// <param name="agentDonePrs">Current agent:done PR list for this template. May be empty.</param>
    /// <param name="effectiveConcurrencyLimit">Max in-flight updates for this repo. Clamped to ≥ 1.</param>
    /// <param name="ct">Cancellation token for the mergeability checks. The update HTTP calls
    /// use <see cref="CancellationToken.None"/> internally so they complete independently.</param>
    Task ExecuteAsync(
        IRepositoryProvider repoProvider,
        string repoProviderId,
        IReadOnlyList<PullRequestSummary> agentDonePrs,
        int effectiveConcurrencyLimit,
        CancellationToken ct);
}
