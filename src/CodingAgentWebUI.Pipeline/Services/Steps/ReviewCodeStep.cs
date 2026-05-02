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

        await context.AgentExecution.ExecuteCodeReviewAsync(
            context.Run, context.Config, context.AgentProvider,
            context.Issue!, context.ParsedIssue!,
            context.Cts,
            context.TransitionTo,
            context.EmitOutputLine, context.NotifyChange, ct,
            resolvedReviewers);

        return StepResult.Continue;
    }
}
