namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Outcome of a dispatch eligibility check for a single work item.
/// </summary>
internal enum EligibilityOutcome
{
    /// <summary>Item is eligible for dispatch — template resolved, within concurrency limits, PVC available.</summary>
    Eligible,

    /// <summary>Item's selector group is at its configured concurrency limit.</summary>
    AtConcurrencyLimit,

    /// <summary>Item requires a kiro PVC but none are available.</summary>
    NoPvcAvailable,

    /// <summary>No job template could be resolved for the item's selector.</summary>
    NoTemplate
}

/// <summary>
/// Result of evaluating dispatch eligibility for a single work item.
/// </summary>
internal sealed record EligibilityResult(
    EligibilityOutcome Outcome,
    JobTemplate? Template = null,
    string? EffectiveSelector = null,
    bool IsKiroAgent = false,
    string? ErrorMessage = null)
{
    public static EligibilityResult Eligible(JobTemplate template, string effectiveSelector, bool isKiroAgent)
        => new(EligibilityOutcome.Eligible, template, effectiveSelector, isKiroAgent);

    public static EligibilityResult AtConcurrencyLimit()
        => new(EligibilityOutcome.AtConcurrencyLimit);

    public static EligibilityResult NoPvcAvailable()
        => new(EligibilityOutcome.NoPvcAvailable);

    public static EligibilityResult NoTemplate(string errorMessage)
        => new(EligibilityOutcome.NoTemplate, ErrorMessage: errorMessage);
}
