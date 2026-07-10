namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of <see cref="Interfaces.IDispatchOrchestrationService.DistributeAndFinalizeAsync"/>.
/// Encapsulates the outcome of the distribute → confirm/revert lifecycle.
/// </summary>
/// <param name="Success">Whether the distribution succeeded.</param>
/// <param name="Queued">
/// When <c>true</c>, the item was queued as Pending (no idle agent available).
/// The label swap to <c>agent:in-progress</c> is deferred to the drain service.
/// </param>
/// <param name="ErrorMessage">Error details when <paramref name="Success"/> is false.</param>
/// <remarks>
/// <c>WorkItemId</c> from <see cref="DistributionResult"/> is intentionally omitted —
/// neither caller uses it after the distribute+confirm/revert flow. If a future caller
/// needs it, extend this record rather than returning the full <see cref="DistributionResult"/>.
/// </remarks>
public record DispatchOutcome(bool Success, bool Queued, string? ErrorMessage);
