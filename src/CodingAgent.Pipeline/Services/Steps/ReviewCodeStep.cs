using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Resolves reviewer configurations and delegates to
/// <see cref="AgentPhaseExecutor.ExecuteCodeReviewAsync"/>.
/// </summary>
public sealed class ReviewCodeStep : IPipelineStep
{
    public string StepName => "ReviewCode";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ReviewCode");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        if (context.Run.CodegenSessionId is not null)
            activity?.SetTag("pipeline.codegen_session_id", context.Run.CodegenSessionId);

        // The API resolves the reviewers from the template's stack labels and the job assignment carries them.
        // The project's reviewers join the others, so they run concurrently with them
        var resolvedReviewers = ProjectReview.AddReviewers(
            context.PreResolvedReviewerConfigs ?? [], context.ProjectReviewers, context.ProjectReviewRepositories);

        // Store resolved reviewers so PostReviewFindingsStep can access them for per-agent retry
        context.ResolvedReviewerConfigs = resolvedReviewers;

        var phaseContext = context.BuildAgentPhaseContext();

        await context.AgentExecution.ExecuteCodeReviewAsync(
            phaseContext, ct, resolvedReviewers);

        return StepResult.Continue;
    }
}
