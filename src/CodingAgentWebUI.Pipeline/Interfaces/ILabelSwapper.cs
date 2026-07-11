using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Unified label management interface. The caller specifies what kind of entity
/// the label belongs to; the implementation routes to the correct provider internally.
/// </summary>
public interface ILabelSwapper
{
    /// <summary>
    /// Swaps the agent label on an entity: removes all existing agent labels, then adds <paramref name="newLabel"/>.
    /// Routes to the correct provider based on <paramref name="targetKind"/>.
    /// </summary>
    Task SwapLabelAsync(string providerConfigId, string identifier, string newLabel,
        LabelTargetKind targetKind, CancellationToken ct);

    /// <summary>
    /// Ensures agent status labels exist for the given target kind.
    /// Routes to IIssueProvider.EnsureAgentLabelsAsync for Issues,
    /// or IRepositoryProvider.EnsureAgentLabelsForPullRequestsAsync for PRs.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task<bool> EnsureAgentLabelsAsync(string providerConfigId,
        LabelTargetKind targetKind, CancellationToken ct);

    /// <summary>
    /// Backward-compatible overload for existing call sites (routes to Issue).
    /// </summary>
    Task SwapLabelAsync(string providerConfigId, string identifier, string newLabel, CancellationToken ct)
        => SwapLabelAsync(providerConfigId, identifier, newLabel, LabelTargetKind.Issue, ct);
}
