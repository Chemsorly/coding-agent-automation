namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Maps each <see cref="PipelineStep"/> to its logical execution order within
/// the respective pipeline type. Used for HighWaterMark comparisons instead of
/// raw enum ordinals (which don't reflect execution sequence).
/// </summary>
public static class StepOrder
{
    /// <summary>
    /// Implementation pipeline execution order.
    /// Decomposition steps are also included with their own ordering.
    /// Terminal states (Failed, Cancelled) are excluded — they return -1.
    /// </summary>
    private static readonly Dictionary<PipelineStep, int> _order = new()
    {
        // Implementation pipeline
        [PipelineStep.Created] = 0,
        [PipelineStep.CloningRepository] = 1,
        [PipelineStep.RunningEnvironmentSetup] = 2,
        [PipelineStep.SyncingBrainRepoPreRun] = 3,
        [PipelineStep.CreatingBranch] = 4,
        [PipelineStep.VerifyingBaseline] = 5,
        [PipelineStep.AnalyzingCode] = 6,
        [PipelineStep.ReviewingAnalysis] = 7,
        [PipelineStep.PostingAnalysis] = 8,
        [PipelineStep.GeneratingCode] = 9,
        [PipelineStep.ReviewingCode] = 10,
        [PipelineStep.RunningQualityGates] = 11,
        [PipelineStep.PreparingForPullRequest] = 12,
        [PipelineStep.CreatingPullRequest] = 13,
        [PipelineStep.ReflectingOnRun] = 14,
        [PipelineStep.SyncingBrainRepoPostRun] = 15,

        // Decomposition pipeline
        [PipelineStep.ExtractingLinkedIssues] = 1,
        [PipelineStep.DownloadingOpenIssues] = 2,
        [PipelineStep.ExploringCodebase] = 3,
        [PipelineStep.GeneratingPlan] = 4,
        [PipelineStep.ReviewingPlan] = 5,
        [PipelineStep.PostingPlan] = 6,
        [PipelineStep.GeneratingSubIssues] = 7,
        [PipelineStep.CreatingIssues] = 8,
        [PipelineStep.PostingSummary] = 9,
        [PipelineStep.PostingFindings] = 10,

        // Shared terminal success state
        [PipelineStep.Completed] = 100,
    };

    /// <summary>
    /// Returns the logical execution order for a pipeline step.
    /// Returns -1 for unknown/terminal-failure states (Failed, Cancelled).
    /// </summary>
    public static int GetOrder(PipelineStep step) => _order.TryGetValue(step, out var o) ? o : -1;
}
