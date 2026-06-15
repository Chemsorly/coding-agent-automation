namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Detects existing agent pull requests for the issue (rework mode detection). Non-fatal on failure.
/// </summary>
internal sealed class DetectReworkStep : IPipelineStep
{
    public string StepName => "DetectRework";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Run.LinkedPullRequest is not null)
            return StepResult.Continue; // Already set (e.g., from job assignment)

        try
        {
            var agentPrs = await context.RepoProvider.GetAgentPullRequestsAsync(context.Run.IssueIdentifier, ct);
            if (agentPrs.Count > 0)
            {
                // TODO: Only the most recent PR is checked; older draft PRs for the same issue remain orphaned. Consider iterating all draft PRs in agentPrs to close them.
                var selectedPr = agentPrs.OrderByDescending(pr => pr.Number).First();

                // Close stale draft PRs to prevent orphaned accumulation
                if (selectedPr.IsDraft)
                {
                    await context.RepoProvider.ClosePullRequestAsync(selectedPr.Number, ct);
                    context.Callbacks.EmitOutputLine($"🗑️ Closed stale draft PR #{selectedPr.Number}");
                    context.Logger.Information(
                        "Pipeline {RunId} closed stale draft PR #{PrNumber} for issue {IssueIdentifier}",
                        context.Run.RunId, selectedPr.Number, context.Run.IssueIdentifier);
                }
                else
                {
                    context.Run.LinkedPullRequest = selectedPr;
                    context.Callbacks.EmitOutputLine($"🔄 Rework mode: updating existing PR #{selectedPr.Number}");
                    context.Logger.Information(
                        "Pipeline {RunId} detected existing agent PR #{PrNumber} for issue {IssueIdentifier}, entering rework mode",
                        context.Run.RunId, selectedPr.Number, context.Run.IssueIdentifier);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex,
                "Pipeline {RunId} failed to detect agent PRs for issue {IssueIdentifier}, falling back to new-issue flow",
                context.Run.RunId, context.Run.IssueIdentifier);
        }

        return StepResult.Continue;
    }
}
