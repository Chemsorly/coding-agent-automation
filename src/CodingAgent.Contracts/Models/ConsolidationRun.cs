namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The type of consolidation loop being executed.
/// </summary>
public enum ConsolidationRunType
{
    BrainConsolidation,
    RefactoringDetection,
    HarnessSuggestions
}

/// <summary>
/// Result returned by <see cref="IConsolidationService.TriggerAsync"/> after
/// <c>ConsolidationRun</c> and <c>ConsolidationRunStatus</c> were deleted (issue #3032).
/// Carries only the fields that callers actually consume.
/// </summary>
public sealed record ConsolidationTriggerResult(
    string RunId,
    ConsolidationRunType Type,
    string? TemplateId,
    string? TemplateName,
    string? ProjectId,
    string? ProjectName,
    DateTimeOffset StartedAtUtc,
    string? WorkItemId);
