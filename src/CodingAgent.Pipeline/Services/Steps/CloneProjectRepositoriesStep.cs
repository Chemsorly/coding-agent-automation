using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Clones additional project repositories into subdirectories for cross-repo decomposition.
/// Runs after <see cref="CloneRepositoryStep"/> (primary repo already at workspace root).
/// Skips gracefully when:
/// - No <see cref="PipelineStepContext.ProjectContext"/> is present (per-template decomposition)
/// - No <see cref="PipelineStepContext.AdditionalRepoProviders"/> are configured
///
/// Each additional repo is cloned read-only into <c>{workspace}/repos/{template-name}/</c> by
/// <see cref="ProjectRepositoryCloner"/>. Clone failures are non-critical: the repo is marked unavailable via
/// <see cref="Models.RepositoryTarget.LocalPath"/> remaining null, and the pipeline continues with whatever repos are
/// available.
/// </summary>
public sealed class CloneProjectRepositoriesStep : IPipelineStep
{
    public string StepName => "CloneProjectRepositories";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CloneProjectRepositories");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        // Skip if not cross-repo decomposition
        if (context.ProjectContext is null || context.AdditionalRepoProviders is not { Count: > 0 } providers)
            return StepResult.Continue;

        await ProjectRepositoryCloner.CloneAsync(providers, context.ProjectContext.Repositories, "repos", context, ct);
        return StepResult.Continue;
    }
}
