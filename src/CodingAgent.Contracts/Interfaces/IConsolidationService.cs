using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages consolidation loop execution: triggering runs.
/// </summary>
public interface IConsolidationService
{
    /// <summary>
    /// Triggers a consolidation run of the specified type. Returns a result record describing
    /// the created run, or <c>null</c> if rejected (e.g., duplicate already running, no idle
    /// agent available).
    /// </summary>
    /// <param name="type">The type of consolidation loop to execute.</param>
    /// <param name="templateId">The Pipeline Job Template ID.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="autoDispatch">When true, created refactoring issues will also receive the <c>agent:next</c> label.</param>
    /// <param name="focus">Which detection agent a refactoring scan runs; ignored by the other consolidation types.</param>
    /// <returns>A <see cref="ConsolidationTriggerResult"/> describing the dispatched run, or <c>null</c> if the trigger was rejected.</returns>
    Task<ConsolidationTriggerResult?> TriggerAsync(ConsolidationRunType type, TemplateId? templateId, CancellationToken ct, bool autoDispatch = false, RefactoringScanFocus focus = RefactoringScanFocus.All);

    /// <summary>
    /// Starts a consolidation run for the automatic trigger: the run records <c>InitiatedByConstants.ConsolidationAuto</c>
    /// and never adds <c>agent:next</c> to created issues. Returns null when the run was rejected (already running,
    /// configuration error, template not found).
    /// </summary>
    Task<ConsolidationTriggerResult?> TriggerAutomaticAsync(ConsolidationRunType type, TemplateId? templateId, CancellationToken ct);

    /// <summary>
    /// Fired when any consolidation run changes state (created, completed, or failed).
    /// </summary>
    event Action? OnChange;
}
