namespace CodingAgent.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>
    /// Single authoritative dispatch: routes <paramref name="p"/> to the correct
    /// <c>Create*</c> factory method based on <see cref="PipelineRunCreationParams.RunType"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Callers are responsible for setting <see cref="PipelineRunCreationParams.RunType"/> correctly
    /// before calling this method — it is the dispatch key. This method does NOT override
    /// <see cref="PipelineRunCreationParams.IssueProviderConfigId"/>; callers that need the
    /// consolidation sentinel must set it themselves (e.g. <c>FromDistributionRequest</c>).
    /// </para>
    /// <para>
    /// Consolidation routing note: completion-strategy selection in <c>AgentJobLifecycleService</c>
    /// keys on <c>IssueProviderConfigId == ConsolidationConstants.ProviderConfigId</c>, not on
    /// <c>RunType</c>. Callers that need the sentinel (e.g. <c>FromDistributionRequest</c>) must
    /// set it before calling this method; this factory passes it through unchanged.
    /// </para>
    /// </remarks>
    public static PipelineRun CreateForRunType(PipelineRunCreationParams p)
    {
        ArgumentNullException.ThrowIfNull(p);
        // TODO: [WARNING] The explicit PipelineRunType.Consolidation arm below is redundant — it
        // produces exactly the same result as the _ arm. Its sole purpose is to document that
        // Consolidation intentionally routes through CreateImplementation. A future maintainer
        // adding a new PipelineRunType value cannot distinguish "deliberate default" from "forgot
        // to add an arm" for Consolidation. Consider replacing it with a comment on the _ arm.
        return p.RunType switch
        {
            PipelineRunType.Review => CreateReview(p),
            PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition => CreateDecomposition(p),
            PipelineRunType.Consolidation => CreateImplementation(p), // intentional: Consolidation uses the implementation pipeline; RunType is passed through by CreateCore
            _ => CreateImplementation(p)
        };
    }

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
            throw new ArgumentOutOfRangeException(nameof(p), p.RunType, "RunType must be DecompositionAnalysis or Decomposition.");
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
            IssueUrl = p.IssueUrl,
            IssueProviderConfigId = p.IssueProviderConfigId,
            RepoProviderConfigId = p.RepoProviderConfigId,
            StartedAt = now.UtcDateTime,
            StartedAtOffset = now,
            // LastStepChangeAt is intentionally set independently from `now` — when startedAt is provided,
            // these will differ (preserves pre-refactor behavior).
            LastStepChangeAt = DateTimeOffset.UtcNow,
            CurrentStep = PipelineStep.Created,
            InitiatedBy = p.InitiatedBy,
            RunType = p.RunType,
            AgentId = p.AgentId?.Value,
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
