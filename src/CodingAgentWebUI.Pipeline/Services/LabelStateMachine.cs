using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Defines the valid label transitions for the agent pipeline state machine.
/// Validates transitions at runtime (warn-only, fail-open) to detect invalid label swaps
/// without blocking production execution.
/// </summary>
public static class LabelStateMachine
{
    private static readonly ILogger Logger = Log.ForContext(typeof(LabelStateMachine));

    /// <summary>
    /// Valid transitions: maps a current label to the set of labels it can transition to.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ValidTransitions =
        new Dictionary<string, IReadOnlySet<string>>
        {
            [AgentLabels.Next] = new HashSet<string>
            {
                AgentLabels.InProgress
            },

            [AgentLabels.InProgress] = new HashSet<string>
            {
                AgentLabels.Done,
                AgentLabels.Error,
                AgentLabels.Cancelled,
                AgentLabels.NeedsRefinement,
                AgentLabels.WontDo,
                AgentLabels.EpicReview,
                // Dispatch-failure reverts: restore label to pre-dispatch state
                AgentLabels.Next,
                AgentLabels.Epic,
                AgentLabels.EpicApproved
            },

            [AgentLabels.Error] = new HashSet<string>
            {
                AgentLabels.Next,
                AgentLabels.InProgress
            },

            [AgentLabels.NeedsRefinement] = new HashSet<string>
            {
                AgentLabels.Next,
                AgentLabels.InProgress
            },

            [AgentLabels.Cancelled] = new HashSet<string>
            {
                AgentLabels.Next,
                AgentLabels.InProgress
            },

            [AgentLabels.Done] = new HashSet<string>
            {
                AgentLabels.Next
            },

            [AgentLabels.WontDo] = new HashSet<string>
            {
                AgentLabels.Next,
                AgentLabels.InProgress
            },

            [AgentLabels.Epic] = new HashSet<string>
            {
                AgentLabels.InProgress
            },

            [AgentLabels.EpicReview] = new HashSet<string>
            {
                AgentLabels.EpicApproved,
                AgentLabels.Cancelled
            },

            [AgentLabels.EpicApproved] = new HashSet<string>
            {
                AgentLabels.InProgress
            }
        };

    /// <summary>
    /// Returns whether the transition from <paramref name="currentLabel"/> to <paramref name="targetLabel"/>
    /// is valid according to the state machine. Returns <c>true</c> if <paramref name="currentLabel"/> is null
    /// (unknown source — validation skipped).
    /// </summary>
    public static bool IsValidTransition(string? currentLabel, string targetLabel)
    {
        if (currentLabel is null)
            return true; // Unknown source — cannot validate

        if (!ValidTransitions.TryGetValue(currentLabel, out var allowed))
            return false; // Current label not in state machine (unexpected state)

        return allowed.Contains(targetLabel);
    }

    /// <summary>
    /// Validates the transition and logs a warning if invalid.
    /// Never throws — fail-open by design (observational only).
    /// </summary>
    public static void ValidateTransition(string? currentLabel, string targetLabel)
    {
        if (currentLabel is null)
            return; // Unknown source — skip validation

        if (!IsValidTransition(currentLabel, targetLabel))
        {
            Logger.Warning(
                "Invalid label transition detected: {CurrentLabel} → {TargetLabel}. " +
                "This transition is not in the valid state machine map",
                currentLabel, targetLabel);
        }
    }
}
