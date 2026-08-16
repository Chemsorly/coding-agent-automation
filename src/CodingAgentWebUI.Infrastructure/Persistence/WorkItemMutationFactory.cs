using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence;

/// <summary>
/// Factory for terminal-state mutation actions applied to <see cref="WorkItemEntity"/>
/// during status transitions. Centralises the CompletedAt + FailureReason + ErrorMessage
/// assignment that was previously duplicated across multiple files.
/// </summary>
/// <remarks>
/// All three methods return an <see cref="Action{T}"/> intended to be passed as the
/// <c>mutate</c> delegate to <c>WorkItemTransitionService.TransitionAsync</c>.
/// <para>
/// <see cref="Failed"/> uses null-coalescing assignment (<c>??=</c>) for
/// <see cref="WorkItemEntity.FailureReason"/> so that recovery paths that pre-set a reason
/// (e.g., <c>RunLifecycleManager</c>'s infrastructure-failure recovery) are not silently
/// overwritten by a subsequent terminal transition.
/// </para>
/// </remarks>
public static class WorkItemMutationFactory
{
    private const string DefaultFailureMessage = "Job failed without specific error information";

    /// <summary>
    /// Returns a mutation action for a <see cref="WorkItemStatus.Failed"/> terminal transition.
    /// Sets <see cref="WorkItemEntity.CompletedAt"/>, <see cref="WorkItemEntity.ErrorMessage"/>,
    /// and (if not already set) <see cref="WorkItemEntity.FailureReason"/>.
    /// </summary>
    /// <param name="errorMessage">
    /// Human-readable failure description. Defaults to <c>"Job failed without specific error
    /// information"</c> when <see langword="null"/>.
    /// </param>
    /// <param name="failureReason">
    /// Structured failure reason. Defaults to <see cref="FailureReason.AgentError"/> when
    /// <see langword="null"/> and <see cref="WorkItemEntity.FailureReason"/> is not already set.
    /// </param>
    public static Action<WorkItemEntity> Failed(
        string? errorMessage = null,
        FailureReason? failureReason = null)
        => item =>
        {
            item.CompletedAt = DateTimeOffset.UtcNow;
            item.ErrorMessage = errorMessage ?? DefaultFailureMessage;
            // TODO: ??= preserves a FailureReason already set by a prior transition (e.g., recovery paths in
            // RunLifecycleManager). This changes semantics compared to the direct = assignments that existed at
            // some call sites before this refactor (DispatchLifecycleService, SignalRWorkDistributor). At those
            // call sites the pre-refactor code used unconditional = and would have overwritten a pre-existing
            // FailureReason; with ??= a pre-existing value is silently preserved instead. Verify that no caller
            // relies on the overwrite behaviour, or introduce a forceOverwrite parameter if explicit override
            // is needed at specific call sites.
            item.FailureReason ??= failureReason ?? FailureReason.AgentError;
        };

    /// <summary>
    /// Returns a mutation action for a <see cref="WorkItemStatus.Succeeded"/> terminal transition.
    /// Sets only <see cref="WorkItemEntity.CompletedAt"/>.
    /// </summary>
    public static Action<WorkItemEntity> Succeeded()
        => item => item.CompletedAt = DateTimeOffset.UtcNow;

    /// <summary>
    /// Returns a mutation action for a <see cref="WorkItemStatus.Cancelled"/> terminal transition.
    /// Sets only <see cref="WorkItemEntity.CompletedAt"/>.
    /// </summary>
    public static Action<WorkItemEntity> Cancelled()
        => item => item.CompletedAt = DateTimeOffset.UtcNow;
}
