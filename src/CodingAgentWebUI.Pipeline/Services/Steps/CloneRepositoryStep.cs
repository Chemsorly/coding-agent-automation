using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Creates the workspace directory, clones the repository, and swaps the agent label to in-progress.
/// </summary>
public sealed class CloneRepositoryStep : IPipelineStep
{
    public string StepName => "CloneRepository";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CloneRepository");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        activity?.SetTag("pipeline.repository", context.Run.RepositoryName);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        var workspacePath = Path.Combine(context.Config.WorkspaceBaseDirectory, context.Run.RunId);
        Directory.CreateDirectory(workspacePath);
        context.Run.WorkspacePath = workspacePath;

        context.Callbacks.TransitionTo(PipelineStep.CloningRepository);
        context.Callbacks.EmitOutputLine($"📋 Cloning repository {context.Run.RepositoryName}...");

        // Skip label swap for agent-dispatched runs — the dispatcher already set agent:in-progress
        // before sending the job assignment. Swapping again would produce a redundant GitHub API call
        // that shows up as a confusing "added agent:in-progress and removed agent:in-progress" event.
        if (string.IsNullOrEmpty(context.Run.AgentId))
            await context.Callbacks.SwapAgentLabel(context.Run.IssueIdentifier, AgentLabels.InProgress, ct);

        return await context.TryCriticalAsync(
            () => context.RepoProvider.CloneAsync(workspacePath, ct), "Repository clone", ct);
    }
}
