using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Creates a new branch (new-issue flow) or checks out an existing branch and merges from base (rework flow).
/// </summary>
public sealed class CreateBranchStep : IPipelineStep
{
    public string StepName => "CreateBranch";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreateBranch");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        context.Callbacks.TransitionTo(PipelineStep.CreatingBranch);

        StepResult result;
        if (context.Run.LinkedPullRequest is not null)
            result = await CheckoutAndMergeAsync(context, ct);
        else
            result = await CreateNewBranchAsync(context, ct);

        activity?.SetTag("pipeline.branch_name", context.Run.BranchName);
        return result;
    }

    private static async Task<StepResult> CheckoutAndMergeAsync(PipelineStepContext context, CancellationToken ct)
    {
        var pr = context.Run.LinkedPullRequest!;
        var run = context.Run;

        // Check PR state before any git operations — fail-open if state check errors.
        // This catches rework and Review runs dispatched against a PR that was already merged
        // or closed while the work item was waiting in the Pending queue.
        // TODO [WARNING] (Correctness): This guard fires only when context.Run.LinkedPullRequest is not null
        // (branch point is ExecuteAsync line ~26: LinkedPullRequest == null → CreateNewBranchAsync).
        // New-issue implementation runs have no LinkedPullRequest and therefore skip this check — which is
        // correct since they have no associated PR to guard. Any future code path that sets LinkedPullRequest
        // on a non-rework run would bypass this guard silently. The guard's applicability is scoped to
        // LinkedPullRequest != null, which correctly covers rework and Review runs as required by #2954.
        var prNum = pr.Number;
        PullRequestState prState;
        try
        {
            prState = await context.RepoProvider.GetPullRequestStateAsync(prNum, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Fail open — if we cannot query the state, proceed with the checkout.
            // The CI polling loop will detect a merged PR on the first poll iteration.
            context.Logger.Debug(ex, "Pipeline {RunId} PR state check failed for PR #{PrNum} — continuing with checkout",
                run.RunId, prNum);
            prState = PullRequestState.Open;
        }

        if (prState == PullRequestState.Merged)
        {
            context.Callbacks.EmitOutputLine($"✅ PR #{prNum} was already merged — skipping checkout, run ends Succeeded");
            context.Logger.Information("Pipeline {RunId} PR #{PrNum} already merged at run start — ending Succeeded",
                run.RunId, prNum);
            run.CurrentStep = PipelineStep.PrMerged;
            run.MarkCompleted();
            context.Callbacks.TransitionTo(PipelineStep.PrMerged);
            return StepResult.Stop;
        }

        if (prState == PullRequestState.Closed)
        {
            context.Callbacks.EmitOutputLine($"🚫 PR #{prNum} was already closed — skipping checkout, run ends Cancelled");
            context.Logger.Information("Pipeline {RunId} PR #{PrNum} closed without merge at run start — ending Cancelled",
                run.RunId, prNum);
            run.FinalLabel = AgentLabels.Cancelled;
            run.CurrentStep = PipelineStep.PrClosed;
            run.MarkCompleted();
            context.Callbacks.TransitionTo(PipelineStep.PrClosed);
            return StepResult.Stop;
        }

        var checkoutResult = await context.TryCriticalAsync(async () =>
        {
            await context.RepoProvider.CheckoutRemoteBranchAsync(context.Run.WorkspacePath!, pr.BranchName, ct);
            context.Run.BranchName = pr.BranchName;
            context.Callbacks.EmitOutputLine($"🌿 Checked out existing branch {context.Run.BranchName}");
            context.Logger.Information("Pipeline {RunId} checked out existing branch {BranchName}",
                context.Run.RunId, context.Run.BranchName);
        }, "Branch checkout", ct);

        if (checkoutResult == StepResult.Stop)
            return StepResult.Stop;

        // Skip merge for review runs — review the PR branch as-is
        if (context.Run.RunType == PipelineRunType.Review)
            return StepResult.Continue;

        return await context.TryCriticalAsync(async () =>
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
        }, "Base branch rebase", ct);
    }

    private static async Task<StepResult> CreateNewBranchAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Issue is null)
        {
            context.Logger.Error("Issue must be fetched before creating a branch (RunId={RunId})", context.Run.RunId);
            throw new InvalidOperationException("Issue must be fetched before creating a branch. Ensure FetchIssueStep runs before CreateBranchStep.");
        }

        context.Callbacks.EmitOutputLine("🌿 Creating branch...");
        return await context.TryCriticalAsync(async () =>
        {
            var branchName = PipelineFormatting.GenerateBranchName(
                context.Run.IssueIdentifier, context.Issue.Title, context.Run.RunId);
            context.Run.BranchName = await context.RepoProvider.CreateBranchAsync(
                context.Run.WorkspacePath!, branchName, ct);
            context.Logger.Information("Pipeline {RunId} branch {BranchName} created",
                context.Run.RunId, context.Run.BranchName);
            context.Callbacks.EmitOutputLine($"🌿 Created branch {context.Run.BranchName}");
        }, "Branch creation", ct);
    }
}
