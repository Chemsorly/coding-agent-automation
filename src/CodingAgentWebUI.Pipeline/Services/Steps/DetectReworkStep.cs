namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Detects existing agent pull requests for the issue (rework mode detection). Non-fatal on failure.
/// </summary>
public sealed class DetectReworkStep : IPipelineStep
{
    public string StepName => "DetectRework";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Run.LinkedPullRequest is not null)
            return StepResult.Continue; // Already set (e.g., from job assignment)

        try
        {
            var agentPrs = await context.RepoProvider.GetAgentPullRequestsAsync(context.Run.IssueIdentifier.Value, ct);
            if (agentPrs.Count > 0)
            {
                // Close all stale draft PRs
                foreach (var pr in agentPrs.Where(p => p.IsDraft))
                {
                    await context.RepoProvider.ClosePullRequestAsync(pr.Number, ct);
                    context.Callbacks.EmitOutputLine($"🗑️ Closed stale draft PR #{pr.Number}");
                    context.Logger.Information(
                        "Pipeline {RunId} closed stale draft PR #{PrNumber} for issue {IssueIdentifier}",
                        context.Run.RunId, pr.Number, context.Run.IssueIdentifier);
                }

                // TODO: This relies on GetAgentPullRequestsAsync filtering by open state at the provider level. If a future provider returns non-open PRs, add an explicit guard here to verify the candidate is still open before entering rework mode.
                // Select the highest-numbered non-draft open PR for rework
                var candidate = agentPrs
                    .Where(p => !p.IsDraft)
                    .OrderByDescending(p => p.Number)
                    .FirstOrDefault();

                if (candidate is not null)
                {
                    context.Run.LinkedPullRequest = candidate;
                    context.Callbacks.EmitOutputLine($"🔄 Rework mode: updating existing PR #{candidate.Number}");
                    context.Logger.Information(
                        "Pipeline {RunId} detected existing agent PR #{PrNumber} for issue {IssueIdentifier}, entering rework mode",
                        context.Run.RunId, candidate.Number, context.Run.IssueIdentifier);
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
