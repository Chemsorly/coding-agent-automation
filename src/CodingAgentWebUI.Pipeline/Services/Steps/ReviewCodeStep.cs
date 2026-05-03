using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Resolves reviewer configurations and delegates to
/// <see cref="AgentExecutionOrchestrator.ExecuteCodeReviewAsync"/>.
/// </summary>
internal sealed class ReviewCodeStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        IReadOnlyList<ReviewerConfiguration> resolvedReviewers;
        if (context.PreResolvedReviewerConfigs is not null)
        {
            resolvedReviewers = context.PreResolvedReviewerConfigs;
        }
        else
        {
            var allReviewerConfigs = await context.ConfigStore.LoadReviewerConfigsAsync(ct);
            var reviewerResolver = new ReviewerResolver();
            var repoConfigs = await context.ConfigStore.LoadProviderConfigsAsync(ProviderKind.Repository, ct);
            var repoConfigForLabels = repoConfigs.FirstOrDefault(c => c.Id == context.Run.RepoProviderConfigId);
            var requiredLabelsForReview = LabelResolver.ResolveRequiredLabels(repoConfigForLabels, context.Config);
            resolvedReviewers = reviewerResolver.Resolve(allReviewerConfigs, requiredLabelsForReview);
        }

        var phaseContext = new AgentPhaseContext
        {
            Run = context.Run,
            Config = context.Config,
            AgentProvider = context.AgentProvider,
            Issue = context.Issue ?? throw new InvalidOperationException("Issue must be fetched before code review."),
            ParsedIssue = context.ParsedIssue ?? throw new InvalidOperationException("ParsedIssue must be set before code review."),
            IssueOps = context.IssueOps,
            Callbacks = context.Callbacks,
            OrchestratorCts = context.Cts
        };

        await context.AgentExecution.ExecuteCodeReviewAsync(
            phaseContext, ct, resolvedReviewers);

        return StepResult.Continue;
    }
}
