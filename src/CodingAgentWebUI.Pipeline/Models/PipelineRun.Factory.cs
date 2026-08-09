namespace CodingAgentWebUI.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for an implementation (issue → code → PR) workflow.
    /// </summary>
    // TODO: Consider adding a RunType guard here (if p.RunType != PipelineRunType.Implementation throw)
    // similar to CreateDecomposition, to prevent callers from accidentally passing the wrong RunType.
    // Pre-refactor, CreateImplementation enforced RunType = Implementation internally; now callers must
    // set it explicitly, and an incorrect value silently produces a mistyped run.
    public static PipelineRun CreateImplementation(PipelineRunCreationParams p) => CreateCore(p);

    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for a PR review (PR → code review → comment) workflow.
    /// </summary>
    // TODO: Consider adding a RunType guard here (if p.RunType != PipelineRunType.Review throw)
    // similar to CreateDecomposition, to prevent callers from accidentally passing the wrong RunType.
    // Pre-refactor, CreateReview enforced RunType = Review internally; now callers must set it
    // explicitly, and an incorrect value silently produces a mistyped run.
    public static PipelineRun CreateReview(PipelineRunCreationParams p) => CreateCore(p);

    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for a decomposition (epic → sub-issues) workflow.
    /// </summary>
    /// <param name="p">
    /// Creation parameters. <see cref="PipelineRunCreationParams.RunType"/> must be
    /// <see cref="PipelineRunType.DecompositionAnalysis"/> or <see cref="PipelineRunType.Decomposition"/>.
    /// </param>
    public static PipelineRun CreateDecomposition(PipelineRunCreationParams p)
    {
        if (p.RunType != PipelineRunType.DecompositionAnalysis && p.RunType != PipelineRunType.Decomposition)
            throw new ArgumentOutOfRangeException(nameof(p.RunType), p.RunType, "Must be DecompositionAnalysis or Decomposition.");
        return CreateCore(p);
    }

    /// <summary>Shared construction logic for all factory methods.</summary>
    private static PipelineRun CreateCore(PipelineRunCreationParams p)
    {
        var now = p.StartedAt ?? DateTimeOffset.UtcNow;
#pragma warning disable CS0618
        return new PipelineRun
        {
            RunId = p.RunId,
            IssueIdentifier = p.IssueIdentifier,
            IssueTitle = p.IssueTitle,
            IssueProviderConfigId = p.IssueProviderConfigId,
            RepoProviderConfigId = p.RepoProviderConfigId,
            StartedAt = now.UtcDateTime,
            StartedAtOffset = now,
            // LastStepChangeAt is intentionally set independently from `now` — when startedAt is provided,
            // these will differ (matches original AgentJobDispatcher behavior).
            LastStepChangeAt = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.Created,
            InitiatedBy = p.InitiatedBy,
            RunType = p.RunType,
            AgentId = p.AgentId,
            AgentProviderConfigId = p.AgentProviderConfigId,
            BrainProviderConfigId = p.BrainProviderConfigId,
            ReviewPrBranchName = p.ReviewPrBranchName,
            ReviewPrTargetBranch = p.ReviewPrTargetBranch,
            ReviewPrUrl = p.ReviewPrUrl,
            ReviewPrDescription = p.ReviewPrDescription,
            ReviewPrAuthor = p.ReviewPrAuthor,
            LinkedIssueContexts = p.LinkedIssueContexts,
            DecompositionSource = p.DecompositionSource
        };
#pragma warning restore CS0618
    }
}
