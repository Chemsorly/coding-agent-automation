using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Clones the project's other repositories read-only into <see cref="AgentWorkspacePaths.ProjectReviewRepositoriesDirectory"/>
/// for the project reviewers, right before the code review (see <see cref="PipelineProject.ProjectReviewEnabled"/>).
/// The folder is inside the agent's metadata directory, so the clones never show up in the diff or a commit, and no
/// agent but the project reviewers is told about them.
/// Skips when the project review is off, the project has no other repository, or the run has no code review (an
/// implementation run with <c>CodeReview.MaxIterations = 0</c>).
/// </summary>
public sealed class CloneProjectReviewRepositoriesStep : IPipelineStep
{
    public string StepName => "CloneProjectReviewRepositories";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.ProjectReviewers.Count == 0
            || context.ProjectReviewRepositories is not { Count: > 0 } repositories
            || context.AdditionalRepoProviders is not { Count: > 0 } providers)
            return StepResult.Continue;

        if (context.Run.RunType != PipelineRunType.Review && context.Config.CodeReview.MaxIterations <= 0)
            return StepResult.Continue;

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CloneProjectReviewRepositories");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        await ProjectRepositoryCloner.CloneAsync(
            providers, repositories, AgentWorkspacePaths.ProjectReviewRepositoriesDirectory, context, ct);
        return StepResult.Continue;
    }
}
