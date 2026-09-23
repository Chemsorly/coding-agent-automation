using CodingAgent.Pipeline.Models;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Applies key-value metadata from step transitions to a <see cref="PipelineRun"/>.
/// Extracted from <see cref="AgentJobLifecycleService"/> to keep the 24-branch switch
/// in a dedicated, testable class that grows with new metadata keys without bloating the lifecycle service.
/// Keys use a flat naming convention (e.g., "BranchName", "BaselineHealthPassed").
/// </summary>
internal static class StepMetadataApplier
{
    /// <summary>
    /// Applies each key/value pair in <paramref name="metadata"/> to the corresponding field on <paramref name="run"/>.
    /// Unknown keys are silently ignored.
    /// </summary>
    internal static void Apply(PipelineRun run, Dictionary<string, string> metadata)
    {
        // Collect code review counts for single-pass atomic update
        int? pendingCritical = null, pendingWarning = null, pendingSuggestion = null;

        foreach (var (key, value) in metadata)
        {
            switch (key)
            {
                case "BranchName":
                    run.BranchName = value;
                    break;
                case "BaselineHealthPassed":
                    run.BaselineHealthPassed = TryParseBool(value);
                    break;
                case "AnalysisSkipped":
                    run.AnalysisSkipped = TryParseBool(value) == true;
                    break;
                case "FilesChangedCount":
                    run.FilesChangedCount = TryParseInt(value) ?? run.FilesChangedCount;
                    break;
                case "LinesAdded":
                    run.LinesAdded = TryParseInt(value) ?? run.LinesAdded;
                    break;
                case "LinesRemoved":
                    run.LinesRemoved = TryParseInt(value) ?? run.LinesRemoved;
                    break;
                case "CodeReviewIterationsCompleted":
                    run.CodeReviewIterationsCompleted = TryParseInt(value) ?? run.CodeReviewIterationsCompleted;
                    break;
                case "CodeReviewIterationsTotal":
                    run.CodeReviewIterationsTotal = TryParseInt(value) ?? run.CodeReviewIterationsTotal;
                    break;
                case "CodeReviewIterationInProgress":
                    run.CodeReviewIterationInProgress = TryParseInt(value) ?? run.CodeReviewIterationInProgress;
                    break;
                case "OpenIssuesDownloaded":
                    run.OpenIssuesDownloaded = TryParseInt(value) ?? run.OpenIssuesDownloaded;
                    break;
                case "DecompositionSubIssuesCreated":
                    run.DecompositionSubIssuesCreated = TryParseInt(value) ?? run.DecompositionSubIssuesCreated;
                    break;
                case "DecompositionSubIssuesAttempted":
                    run.DecompositionSubIssuesAttempted = TryParseInt(value) ?? run.DecompositionSubIssuesAttempted;
                    break;
                case "RetryCount":
                    run.RetryCount = TryParseInt(value) ?? run.RetryCount;
                    break;
                case "InfrastructureRetryCount":
                    run.InfrastructureRetryCount = TryParseInt(value) ?? run.InfrastructureRetryCount;
                    break;
                case "TotalTokens":
                    run.TotalTokens = TryParseLong(value) ?? run.TotalTokens;
                    break;
                case "TotalCost":
                    run.TotalCost = TryParseDecimalInvariant(value) ?? run.TotalCost;
                    break;
                case "CodeReviewCriticalCount":
                    pendingCritical = TryParseInt(value);
                    break;
                case "CodeReviewWarningCount":
                    pendingWarning = TryParseInt(value);
                    break;
                case "CodeReviewSuggestionCount":
                    pendingSuggestion = TryParseInt(value);
                    break;
                case "CodeReviewAgentsRun":
                    run.CodeReviewAgentsRun = value.Split('\x1F', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "PullRequestUrl":
                    if (!string.IsNullOrEmpty(value))
                        run.PullRequestUrl = value;
                    break;
            }
        }

        // Apply code review counts atomically in a single call (avoids iteration-order dependency)
        if (pendingCritical.HasValue || pendingWarning.HasValue || pendingSuggestion.HasValue)
        {
            run.SetCodeReviewCounts(
                pendingCritical ?? run.CodeReviewCriticalCount,
                pendingWarning ?? run.CodeReviewWarningCount,
                pendingSuggestion ?? run.CodeReviewSuggestionCount);
        }
    }

    private static int? TryParseInt(string value) =>
        int.TryParse(value, out var n) ? n : null;

    private static long? TryParseLong(string value) =>
        long.TryParse(value, out var n) ? n : null;

    private static bool? TryParseBool(string value) =>
        bool.TryParse(value, out var b) ? b : null;

    private static decimal? TryParseDecimalInvariant(string value) =>
        decimal.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null;
}
