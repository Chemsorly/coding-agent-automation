using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Creates a new branch (new-issue flow) or checks out an existing branch and merges from base (rework flow).
/// </summary>
internal sealed class CreateBranchStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.TransitionTo(PipelineStep.CreatingBranch);

        if (context.Run.LinkedPullRequest is not null)
            return await CheckoutAndMergeAsync(context, ct);

        return await CreateNewBranchAsync(context, ct);
    }

    private static async Task<StepResult> CheckoutAndMergeAsync(PipelineStepContext context, CancellationToken ct)
    {
        var pr = context.Run.LinkedPullRequest!;
        try
        {
            await context.RepoProvider.CheckoutRemoteBranchAsync(context.Run.WorkspacePath!, pr.BranchName, ct);
            context.Run.BranchName = pr.BranchName;
            context.EmitOutputLine($"🌿 Checked out existing branch {context.Run.BranchName}");
            context.Logger.Information("Pipeline {RunId} checked out existing branch {BranchName}",
                context.Run.RunId, context.Run.BranchName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to checkout branch {BranchName}",
                context.Run.RunId, pr.BranchName);
            await context.FailRunAsync($"Branch checkout failed: {ex.Message}");
            return StepResult.Stop;
        }

        try
        {
            var mergeResult = await context.RepoProvider.MergeFromBaseAsync(context.Run.WorkspacePath!, ct);
            context.Run.MergeConflictFiles = mergeResult.ConflictFiles;
            if (mergeResult.HasConflicts)
            {
                context.EmitOutputLine($"⚠️ Merged from {context.RepoProvider.BaseBranch} with {mergeResult.ConflictFiles.Count} conflict(s)");
                context.Logger.Information("Pipeline {RunId} merged from base with {ConflictCount} conflict(s)",
                    context.Run.RunId, mergeResult.ConflictFiles.Count);
            }
            else
            {
                context.EmitOutputLine($"🔀 Merged from {context.RepoProvider.BaseBranch} (no conflicts)");
                context.Logger.Information("Pipeline {RunId} merged from base (no conflicts)", context.Run.RunId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to merge from base branch", context.Run.RunId);
            await context.FailRunAsync($"Base branch merge failed: {ex.Message}");
            return StepResult.Stop;
        }

        return StepResult.Continue;
    }

    private static async Task<StepResult> CreateNewBranchAsync(PipelineStepContext context, CancellationToken ct)
    {
        // TODO: [WARNING] context.Issue! uses null-forgiving operator without guard. Add explicit null check
        // with descriptive InvalidOperationException if step ordering becomes configurable at runtime.
        context.EmitOutputLine("🌿 Creating branch...");
        try
        {
            var branchName = PipelineFormatting.GenerateBranchName(
                context.Run.IssueIdentifier, context.Issue!.Title, context.Run.RunId);
            context.Run.BranchName = await context.RepoProvider.CreateBranchAsync(
                context.Run.WorkspacePath!, branchName, ct);
            context.Logger.Information("Pipeline {RunId} branch {BranchName} created",
                context.Run.RunId, context.Run.BranchName);
            context.EmitOutputLine($"🌿 Created branch {context.Run.BranchName}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to create branch", context.Run.RunId);
            await context.FailRunAsync($"Branch creation failed: {ex.Message}");
            return StepResult.Stop;
        }

        return StepResult.Continue;
    }
}
