using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Creates a new branch (new-issue flow) or checks out an existing branch and merges from base (rework flow).
/// </summary>
internal sealed class CreateBranchStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        context.Callbacks.TransitionTo(PipelineStep.CreatingBranch);

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
            context.Callbacks.EmitOutputLine($"🌿 Checked out existing branch {context.Run.BranchName}");
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

        // Skip merge for review runs — review the PR branch as-is
        if (context.Run.RunType == PipelineRunType.Review)
            return StepResult.Continue;

        try
        {
            var mergeResult = await context.RepoProvider.MergeFromBaseAsync(context.Run.WorkspacePath!, ct);
            context.Run.MergeConflictFiles = mergeResult.ConflictFiles;
            context.Run.MergeForceResolved = mergeResult.ForceResolved;
            if (mergeResult.HasConflicts && mergeResult.ForceResolved)
            {
                context.Callbacks.EmitOutputLine($"⚠️ Rebase onto {context.RepoProvider.BaseBranch} had {mergeResult.ConflictFiles.Count} conflict(s) — force-resolved using incoming (main wins)");
                context.Logger.Information("Pipeline {RunId} rebase force-resolved {ConflictCount} conflict(s) using incoming",
                    context.Run.RunId, mergeResult.ConflictFiles.Count);
            }
            else if (mergeResult.HasConflicts)
            {
                context.Callbacks.EmitOutputLine($"⚠️ Rebase onto {context.RepoProvider.BaseBranch} failed with {mergeResult.ConflictFiles.Count} conflict(s)");
                context.Logger.Information("Pipeline {RunId} rebase onto base had {ConflictCount} conflict(s)",
                    context.Run.RunId, mergeResult.ConflictFiles.Count);
            }
            else
            {
                context.Callbacks.EmitOutputLine($"🔀 Rebased onto {context.RepoProvider.BaseBranch} (no conflicts)");
                context.Logger.Information("Pipeline {RunId} rebased onto base (no conflicts)", context.Run.RunId);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to rebase onto base branch", context.Run.RunId);
            await context.FailRunAsync($"Base branch rebase failed: {ex.Message}");
            return StepResult.Stop;
        }

        return StepResult.Continue;
    }

    private static async Task<StepResult> CreateNewBranchAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Issue is null)
            throw new InvalidOperationException("Issue must be fetched before creating a branch. Ensure FetchIssueStep runs before CreateBranchStep.");

        context.Callbacks.EmitOutputLine("🌿 Creating branch...");
        try
        {
            var branchName = PipelineFormatting.GenerateBranchName(
                context.Run.IssueIdentifier, context.Issue.Title, context.Run.RunId);
            context.Run.BranchName = await context.RepoProvider.CreateBranchAsync(
                context.Run.WorkspacePath!, branchName, ct);
            context.Logger.Information("Pipeline {RunId} branch {BranchName} created",
                context.Run.RunId, context.Run.BranchName);
            context.Callbacks.EmitOutputLine($"🌿 Created branch {context.Run.BranchName}");
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
