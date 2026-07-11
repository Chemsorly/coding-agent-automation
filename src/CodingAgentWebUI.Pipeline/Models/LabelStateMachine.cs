using Serilog;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Defines the valid label transitions for the agent pipeline lifecycle.
/// This is the single authoritative source of truth for which label transitions are allowed.
/// Validation is observational only — invalid transitions log a warning but do NOT block execution.
/// </summary>
/// <remarks>
/// The valid transition map is documented in <c>.kiro/decisions.md</c>.
/// Human-initiated transitions (re-labeling in GitHub UI) bypass this system and are not validated.
/// The <c>agent:generated</c> label is orthogonal (can coexist with any state) and is excluded.
/// </remarks>
public static class LabelStateMachine
{
    // TODO: Static ILogger captures Log.ForContext at class-load time. If Serilog is not yet
    //       configured (e.g., during unit test startup), this becomes a silent no-op and
    //       ValidateTransition warnings are lost. Consider a lazy pattern or ILogger parameter
    //       if log verification in tests becomes a requirement.
    private static readonly ILogger Logger = Log.ForContext(typeof(LabelStateMachine));

    /// <summary>
    /// The complete map of valid label transitions.
    /// Key = current label, Value = set of valid target labels.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ValidTransitions =
        new Dictionary<string, IReadOnlySet<string>>
        {
            // Implementation flow
            [AgentLabels.Next] = new HashSet<string> { AgentLabels.InProgress },

            [AgentLabels.InProgress] = new HashSet<string>
            {
                AgentLabels.Done,
                AgentLabels.Error,
                AgentLabels.Cancelled,
                AgentLabels.NeedsRefinement,
                AgentLabels.WontDo,
                AgentLabels.EpicReview
            },

            // Recovery transitions (human re-labels for retry)
            [AgentLabels.Error] = new HashSet<string> { AgentLabels.Next, AgentLabels.InProgress },
            [AgentLabels.NeedsRefinement] = new HashSet<string> { AgentLabels.Next, AgentLabels.InProgress },
            [AgentLabels.Cancelled] = new HashSet<string> { AgentLabels.Next, AgentLabels.InProgress },

            // Epic decomposition flow
            [AgentLabels.Epic] = new HashSet<string> { AgentLabels.InProgress },
            [AgentLabels.EpicReview] = new HashSet<string> { AgentLabels.EpicApproved, AgentLabels.Cancelled },
            [AgentLabels.EpicApproved] = new HashSet<string> { AgentLabels.InProgress },
        };

    /// <summary>
    /// Checks whether a transition from <paramref name="currentLabel"/> to <paramref name="targetLabel"/> is valid.
    /// </summary>
    /// <param name="currentLabel">The current agent label on the issue/PR, or null if none is present.</param>
    /// <param name="targetLabel">The target label to transition to.</param>
    /// <returns>True if the transition is valid or if no current label is set; false otherwise.</returns>
    public static bool IsValidTransition(string? currentLabel, string targetLabel)
    {
        // No current label — any target is valid (initial labeling)
        if (currentLabel is null)
            return true;

        // Empty target means "remove all labels" — always valid
        if (string.IsNullOrEmpty(targetLabel))
            return true;

        return ValidTransitions.TryGetValue(currentLabel, out var allowed)
            && allowed.Contains(targetLabel);
    }

    /// <summary>
    /// Validates a label transition and logs a warning if invalid.
    /// Does NOT block execution — this is observational only (fail-open).
    /// </summary>
    /// <param name="currentLabel">The current agent label, or null if none is present.</param>
    /// <param name="targetLabel">The target label to transition to.</param>
    /// <param name="identifier">The issue/PR identifier for log context.</param>
    /// <returns>True if the transition is valid; false if invalid (warning logged).</returns>
    public static bool ValidateTransition(string? currentLabel, string targetLabel, string? identifier = null)
    {
        if (IsValidTransition(currentLabel, targetLabel))
            return true;

        Logger.Warning(
            "Invalid label transition detected: {CurrentLabel} → {TargetLabel} on {Identifier}. " +
            "This may indicate a bug in the pipeline orchestration logic.",
            currentLabel, targetLabel, identifier ?? "(unknown)");

        return false;
    }
}
