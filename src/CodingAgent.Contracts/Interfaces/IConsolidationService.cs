using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages consolidation loop execution: triggering runs and persisting harness suggestions.
/// </summary>
public interface IConsolidationService
{
    /// <summary>
    /// Triggers a consolidation run of the specified type. Returns the created run,
    /// or <c>null</c> if rejected (e.g., duplicate already running, no idle agent available).
    /// </summary>
    /// <param name="type">The type of consolidation loop to execute.</param>
    /// <param name="templateId">The Pipeline Job Template ID (null for harness suggestions which are global).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="autoDispatch">When true, created refactoring issues will also receive the <c>agent:next</c> label.</param>
    /// <returns>The created <see cref="ConsolidationRun"/>, or <c>null</c> if the trigger was rejected.</returns>
    Task<ConsolidationRun?> TriggerAsync(ConsolidationRunType type, TemplateId? templateId, CancellationToken ct, bool autoDispatch = false);

    /// <summary>
    /// Returns the current harness suggestions from the persisted file
    /// (<c>config/pipeline/harness-suggestions.json</c>).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The deserialized suggestions, or <c>null</c> if the file does not exist.</returns>
    Task<HarnessSuggestions?> GetHarnessSuggestionsAsync(CancellationToken ct);

    /// <summary>
    /// Persists harness suggestions to the config file, overwriting any existing content.
    /// </summary>
    /// <param name="suggestions">The suggestions to persist.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SaveHarnessSuggestionsAsync(HarnessSuggestions suggestions, CancellationToken ct);

    /// <summary>
    /// Cleans up orphaned consolidation runs from previous sessions.
    /// Only marks runs as Failed if their RunId is not in <paramref name="activeAgentJobIds"/>
    /// — i.e., no agent is currently working on them.
    /// </summary>
    // TODO [WARNING]: The issue's "Affected Components" section lists CleanupOrphanedRunsAsync in the
    // removal list alongside UpdateRunAsync and TransitionToRunningAsync. However the acceptance
    // criteria only requires "triggering + harness-suggestion access", and this method has a live
    // production caller (ConsolidationRehydrationExtensions.cs:55), so it was intentionally retained.
    // If the intent was to remove it (making the acceptance criteria the authoritative spec), it was
    // missed. Confirm with the issue author whether this method should be moved to a separate interface
    // or removed in a follow-up sub-issue.
    // (review-findings.md — Correctness)
    Task CleanupOrphanedRunsAsync(IReadOnlyCollection<string> activeAgentJobIds, CancellationToken ct);

    /// <summary>
    /// Fired when any consolidation run changes state (created, completed, or failed).
    /// </summary>
    event Action? OnChange;

    /// <summary>
    /// Deletes a consolidation run by ID.
    /// Used by retention cleanup services.
    /// </summary>
    /// <param name="runId">The run ID to delete.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DeleteRunAsync(RunId runId, CancellationToken ct);
}
