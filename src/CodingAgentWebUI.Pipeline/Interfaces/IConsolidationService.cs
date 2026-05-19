using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Manages consolidation loop execution: triggering runs, tracking history,
/// and persisting harness suggestions.
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
    /// <returns>The created <see cref="ConsolidationRun"/>, or <c>null</c> if the trigger was rejected.</returns>
    Task<ConsolidationRun?> TriggerAsync(ConsolidationRunType type, string? templateId, CancellationToken ct);

    /// <summary>
    /// Returns all consolidation runs, ordered by <see cref="ConsolidationRun.StartedAtUtc"/> descending.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<ConsolidationRun>> GetRunHistoryAsync(CancellationToken ct);

    /// <summary>
    /// Returns the most recent run for a given type and template (or global for harness suggestions).
    /// </summary>
    /// <param name="type">The consolidation run type to filter by.</param>
    /// <param name="templateId">The template ID to filter by (null for global/harness suggestions).</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ConsolidationRun?> GetLastRunAsync(ConsolidationRunType type, string? templateId, CancellationToken ct);

    /// <summary>
    /// Updates a run's status and summary after completion.
    /// </summary>
    /// <param name="runId">The unique identifier of the run to update.</param>
    /// <param name="status">The new status for the run.</param>
    /// <param name="summary">Optional summary text describing the outcome.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="totalTokens">Total token count from review/refinement/diff summary calls.</param>
    Task UpdateRunAsync(string runId, ConsolidationRunStatus status, string? summary, CancellationToken ct, long totalTokens = 0);

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
    /// Fired when any consolidation run changes state (created, completed, or failed).
    /// </summary>
    event Action? OnChange;
}
