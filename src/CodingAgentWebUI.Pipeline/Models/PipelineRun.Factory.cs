namespace CodingAgentWebUI.Pipeline.Models;

public sealed partial class PipelineRun
{
    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> with invariant defaults and all init-only properties.
    /// Mutable properties (RepositoryName, ModelName, ProjectId, etc.) should be set after construction.
    /// </summary>
    /// <remarks>
    /// Prefer the variant-specific factory methods (<see cref="CreateImplementation"/>, <see cref="CreateReview"/>,
    /// <see cref="CreateDecomposition"/>) when the run type is known at compile time. This method remains
    /// available for call sites that determine run type at runtime.
    /// </remarks>
    [Obsolete("Use CreateImplementation, CreateReview, or CreateDecomposition when the run type is known at compile time.")]
    public static PipelineRun Create(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string issueProviderConfigId,
        string repoProviderConfigId,
        PipelineRunType runType = PipelineRunType.Implementation,
        DateTimeOffset? startedAt = null,
        string initiatedBy = "manual",
        string? agentId = null,
        string? agentProviderConfigId = null,
        string? brainProviderConfigId = null,
        string? reviewPrBranchName = null,
        string? reviewPrTargetBranch = null,
        string? reviewPrUrl = null,
        string? reviewPrDescription = null,
        string? reviewPrAuthor = null,
        IReadOnlyList<LinkedIssueContext>? linkedIssueContexts = null,
        string? decompositionSource = null)
    {
        return CreateCore(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            RunType = runType,
            StartedAt = startedAt,
            InitiatedBy = initiatedBy,
            AgentId = agentId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            ReviewPrBranchName = reviewPrBranchName,
            ReviewPrTargetBranch = reviewPrTargetBranch,
            ReviewPrUrl = reviewPrUrl,
            ReviewPrDescription = reviewPrDescription,
            ReviewPrAuthor = reviewPrAuthor,
            LinkedIssueContexts = linkedIssueContexts,
            DecompositionSource = decompositionSource
        });
    }

    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for an implementation (issue → code → PR) workflow.
    /// </summary>
    public static PipelineRun CreateImplementation(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string issueProviderConfigId,
        string repoProviderConfigId,
        DateTimeOffset? startedAt = null,
        string initiatedBy = "manual",
        string? agentId = null,
        string? agentProviderConfigId = null,
        string? brainProviderConfigId = null)
    {
        return CreateCore(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            RunType = PipelineRunType.Implementation,
            StartedAt = startedAt,
            InitiatedBy = initiatedBy,
            AgentId = agentId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId
        });
    }

    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for a PR review (PR → code review → comment) workflow.
    /// </summary>
    public static PipelineRun CreateReview(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string issueProviderConfigId,
        string repoProviderConfigId,
        string reviewPrBranchName,
        string reviewPrTargetBranch,
        DateTimeOffset? startedAt = null,
        string initiatedBy = "manual",
        string? agentId = null,
        string? agentProviderConfigId = null,
        string? brainProviderConfigId = null,
        string? reviewPrUrl = null,
        string? reviewPrDescription = null,
        string? reviewPrAuthor = null,
        IReadOnlyList<LinkedIssueContext>? linkedIssueContexts = null)
    {
        return CreateCore(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            RunType = PipelineRunType.Review,
            StartedAt = startedAt,
            InitiatedBy = initiatedBy,
            AgentId = agentId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            ReviewPrBranchName = reviewPrBranchName,
            ReviewPrTargetBranch = reviewPrTargetBranch,
            ReviewPrUrl = reviewPrUrl,
            ReviewPrDescription = reviewPrDescription,
            ReviewPrAuthor = reviewPrAuthor,
            LinkedIssueContexts = linkedIssueContexts
        });
    }

    /// <summary>
    /// Creates a new <see cref="PipelineRun"/> for a decomposition (epic → sub-issues) workflow.
    /// </summary>
    /// <param name="phaseType">Must be <see cref="PipelineRunType.DecompositionAnalysis"/> or <see cref="PipelineRunType.Decomposition"/>.</param>
    public static PipelineRun CreateDecomposition(
        string runId,
        string issueIdentifier,
        string issueTitle,
        string issueProviderConfigId,
        string repoProviderConfigId,
        PipelineRunType phaseType,
        DateTimeOffset? startedAt = null,
        string initiatedBy = "manual",
        string? agentId = null,
        string? agentProviderConfigId = null,
        string? brainProviderConfigId = null,
        string? decompositionSource = null)
    {
        if (phaseType != PipelineRunType.DecompositionAnalysis && phaseType != PipelineRunType.Decomposition)
            throw new ArgumentOutOfRangeException(nameof(phaseType), phaseType, "Must be DecompositionAnalysis or Decomposition.");

        return CreateCore(new PipelineRunCreationParams
        {
            RunId = runId,
            IssueIdentifier = issueIdentifier,
            IssueTitle = issueTitle,
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            RunType = phaseType,
            StartedAt = startedAt,
            InitiatedBy = initiatedBy,
            AgentId = agentId,
            AgentProviderConfigId = agentProviderConfigId,
            BrainProviderConfigId = brainProviderConfigId,
            DecompositionSource = decompositionSource
        });
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
