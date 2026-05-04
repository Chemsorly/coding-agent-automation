using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Creates the workspace directory, clones the repository, and swaps the agent label to in-progress.
/// </summary>
internal sealed class CloneRepositoryStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var workspacePath = Path.Combine(context.Config.Workspace.WorkspaceBaseDirectory, context.Run.RunId);
        Directory.CreateDirectory(workspacePath);
        context.Run.WorkspacePath = workspacePath;

        context.Callbacks.TransitionTo(PipelineStep.CloningRepository);
        context.Callbacks.EmitOutputLine($"📋 Cloning repository {context.Run.RepositoryName}...");
        await context.Callbacks.SwapAgentLabel(context.Run.IssueIdentifier, AgentLabels.InProgress, ct);

        try { await context.RepoProvider.CloneAsync(workspacePath, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to clone repository", context.Run.RunId);
            await context.FailRunAsync($"Repository clone failed: {ex.Message}");
            return StepResult.Stop;
        }

        return StepResult.Continue;
    }
}
