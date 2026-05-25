using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Resolves reviewer configurations and delegates to
/// <see cref="AgentPhaseExecutor.ExecuteCodeReviewAsync"/>.
/// </summary>
internal sealed class ReviewCodeStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ReviewCode");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);

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

        // Store resolved reviewers so PostReviewFindingsStep can access them for per-agent retry
        context.ResolvedReviewerConfigs = resolvedReviewers;

        var phaseContext = context.BuildAgentPhaseContext();

        await context.AgentExecution.ExecuteCodeReviewAsync(
            phaseContext, ct, resolvedReviewers);

        return StepResult.Continue;
    }
}
