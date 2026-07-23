using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Constructs ordered step pipelines for each run type (implementation, review, decomposition).
/// Extracted from <see cref="LocalPipelineExecutor"/> to reduce file size and merge conflict surface.
/// </summary>
internal static class AgentStepPipelineBuilder
{
    /// <summary>
    /// Builds the common step prefix shared by all pipelines:
    /// Clone → EnsureGitignore → [CloneProjectRepositories] → WriteMcpConfig → WriteSteering.
    /// </summary>
    private static List<IPipelineStep> BuildCommonPrefix(JobAssignmentMessage job, bool includeProjectClone = false)
    {
        var steps = new List<IPipelineStep>
        {
            new CloneRepositoryStep(),
            new EnsureAgentGitignoreStep(),
        };
        if (includeProjectClone)
            steps.Add(new CloneProjectRepositoriesStep());
        steps.Add(new WriteMcpConfigStep(job));
        steps.Add(new WriteSteeringStep(job));
        return steps;
    }

    /// <summary>
    /// Builds the full step prefix (common prefix + RunEnvironmentSetup + SyncBrainPreRun + DownloadIssueImages).
    /// Used by agent and decomposition pipelines.
    /// </summary>
    private static List<IPipelineStep> BuildFullPrefix(JobAssignmentMessage job, OrchestratorProxy proxy, ProviderConfig repoConfig, bool includeProjectClone = false)
    {
        var steps = BuildCommonPrefix(job, includeProjectClone);
        steps.Add(new RunEnvironmentSetupStep(job));
        steps.Add(new SyncBrainPreRunStep());
        steps.Add(new DownloadIssueImagesStep(
            ct => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, ct),
            repoConfig));
        return steps;
    }

    /// <summary>
    /// Builds the ordered step pipeline for agent-side execution.
    /// Skips FetchIssueStep (issue data comes from job assignment) and adds MCP config step.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildAgentStepPipeline(
        JobAssignmentMessage job, OrchestratorProxy proxy, ProviderConfig repoConfig)
    {
        var steps = BuildFullPrefix(job, proxy, repoConfig);
        steps.AddRange(PipelineStepFactory.CreateCoreImplementationSteps());
        return steps;
    }

    /// <summary>
    /// Builds the ordered step pipeline for PR review runs.
    /// Shorter sequence: Clone → WriteMcpConfig → WriteSteering → CreateBranch → SyncBrain → DownloadIssueImages → ExtractLinkedIssues → ReviewCode → PostFindings.
    /// Skips analysis, code generation, quality gates, and rework detection.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildReviewStepPipeline(
        JobAssignmentMessage job, OrchestratorProxy proxy, ProviderConfig repoConfig)
    {
        var steps = BuildCommonPrefix(job);
        steps.AddRange([
            new CreateBranchStep(),
            new SyncBrainPreRunStep(),
            new DownloadIssueImagesStep(
                ct => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, ct),
                repoConfig),
            new ExtractLinkedIssuesStep(new IssueDescriptionParser()),
            new ReviewCodeStep(),
            new PostReviewFindingsStep()
        ]);
        return steps;
    }

    /// <summary>
    /// Builds the step pipeline for DecompositionAnalysis (Phase 1).
    /// Sequence: Clone → CloneProjectRepos → WriteMcpConfig → WriteSteering → RunEnvironmentSetup → SyncBrain → DownloadIssueImages → WriteProjectContext → WriteOpenIssueContext → DecompositionAnalysis → PostDecompositionPlan.
    /// IOpenIssueContextWriter is injected into the WriteOpenIssueContextStep via constructor.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionAnalysisStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter,
        OrchestratorProxy proxy,
        ProviderConfig repoConfig)
    {
        var steps = BuildFullPrefix(job, proxy, repoConfig, includeProjectClone: true);
        steps.AddRange([
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionAnalysisStep(),
            new PostDecompositionPlanStep()
        ]);
        return steps;
    }

    /// <summary>
    /// Builds the step pipeline for Decomposition (Phase 2).
    /// Sequence: Clone → CloneProjectRepos → WriteMcpConfig → WriteSteering → RunEnvironmentSetup → SyncBrain → DownloadIssueImages → WriteProjectContext → WriteOpenIssueContext → Decomposition → CreateSubIssues → PostDecompositionSummary.
    /// WriteProjectContextStep is included so the agent has cross-repo routing context
    /// when generating sub-issue JSON files with targetRepository values.
    /// WriteOpenIssueContextStep provides deduplication context for the agent.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter,
        OrchestratorProxy proxy,
        ProviderConfig repoConfig)
    {
        var steps = BuildFullPrefix(job, proxy, repoConfig, includeProjectClone: true);
        steps.AddRange([
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionStep(),
            new CreateSubIssuesStep(),
            new PostDecompositionSummaryStep()
        ]);
        return steps;
    }
}
