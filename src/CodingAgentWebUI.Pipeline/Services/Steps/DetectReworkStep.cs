namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Detects existing agent pull requests for the issue (rework mode detection). Non-fatal on failure.
/// </summary>
internal sealed class DetectReworkStep : IPipelineStep
{
    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Run.LinkedPullRequest is not null)
            return StepResult.Continue; // Already set (e.g., from job assignment)

        try
        {
            var agentPrs = await context.RepoProvider.GetAgentPullRequestsAsync(context.Run.IssueIdentifier, ct);
            if (agentPrs.Count > 0)
            {
                var selectedPr = agentPrs.OrderByDescending(pr => pr.Number).First();
                context.Run.LinkedPullRequest = selectedPr;
                context.Callbacks.EmitOutputLine($"🔄 Rework mode: updating existing PR #{selectedPr.Number}");
                context.Logger.Information(
                    "Pipeline {RunId} detected existing agent PR #{PrNumber} for issue {IssueIdentifier}, entering rework mode",
                    context.Run.RunId, selectedPr.Number, context.Run.IssueIdentifier);
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
