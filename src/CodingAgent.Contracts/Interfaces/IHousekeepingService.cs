using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Evaluates agent:done PRs, evicts resolved in-flight entries, triggers server-side branch
/// updates for PRs that are behind base, and swaps conflicted PRs' linked issues to
/// <c>agent:next</c> for rework dispatch.
/// </summary>
/// <remarks>
/// The service is stateful: it holds an in-flight set per repository across poll ticks
/// to enforce the concurrency limit over the CI run lifetime (not just the HTTP call lifetime).
/// Individual update calls are fire-and-forget — the method returns as soon as eligible
/// PRs have been dispatched, not when their CI runs complete.
///
/// Must be called even when <see cref="HousekeepingRequest.AgentDonePrs"/> is empty so that the
/// eviction pass can free slots for PRs that have since merged.
/// </remarks>
public interface IHousekeepingService
{
    /// <summary>
    /// Evicts resolved in-flight entries, then triggers server-side branch updates
    /// for eligible PRs within the concurrency budget. For conflicted PRs, swaps the
    /// linked issue label to <c>agent:next</c> to trigger rework dispatch.
    /// When <see cref="HousekeepingRequest.BranchCleanupEnabled"/> is true and the cleanup interval
    /// has elapsed, also deletes stale agent branches with no open PR and an inactive issue.
    /// </summary>
    /// <param name="request">
    /// The repository and issue providers, the agent:done PR list, and the concurrency, cooldown
    /// and branch-cleanup settings for this pass.
    /// </param>
    /// <param name="ct">Cancellation token for the mergeability checks. The update HTTP calls
    /// use <see cref="CancellationToken.None"/> internally so they complete independently.</param>
    Task ExecuteAsync(HousekeepingRequest request, CancellationToken ct);
}
