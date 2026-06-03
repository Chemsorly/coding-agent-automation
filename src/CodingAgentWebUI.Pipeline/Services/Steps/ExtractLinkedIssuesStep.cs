using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Extracts linked issue context for PR review runs.
/// Phase 1: Writes pre-fetched linked issue details as files to the workspace (.agent/ directory).
/// Phase 2: Synthesizes IssueDetail and ParsedIssue on the context so ReviewCodeStep works unchanged.
/// </summary>
internal sealed class ExtractLinkedIssuesStep : IPipelineStep
{
    private readonly IssueDescriptionParser _issueParser;

    public ExtractLinkedIssuesStep(IssueDescriptionParser issueParser)
    {
        ArgumentNullException.ThrowIfNull(issueParser);
        _issueParser = issueParser;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ExtractLinkedIssues");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());

        context.Callbacks.TransitionTo(PipelineStep.ExtractingLinkedIssues);

        if (!int.TryParse(context.Run.IssueIdentifier, out var prNumber))
        {
            context.Logger.Warning("PR identifier '{Identifier}' is not a valid integer, skipping linked issue extraction",
                context.Run.IssueIdentifier);
            return StepResult.Continue;
        }

        var prTitle = context.Run.IssueTitle ?? $"PR #{prNumber}";
        var prDescription = context.Run.ReviewPrDescription ?? "";

        // --- Phase 1: Write pre-fetched linked issues to workspace ---
        // Linked issues were fetched at dispatch time (orchestrator-side) and included
        // in the job assignment. The agent doesn't need IIssueProvider credentials.
        var linkedIssues = context.Run.LinkedIssueContexts ?? Array.Empty<LinkedIssueContext>();

        if (linkedIssues.Count > 0)
        {
            var issueContextDir = Path.Combine(context.Run.WorkspacePath!, ".agent");
            Directory.CreateDirectory(issueContextDir);

            foreach (var issue in linkedIssues)
            {
                // Sanitize identifier to prevent path traversal
                var safeId = Path.GetFileName(issue.Identifier);
                if (string.IsNullOrWhiteSpace(safeId) || safeId != issue.Identifier)
                {
                    context.Logger.Warning("Skipping linked issue with unsafe identifier: {Identifier}", issue.Identifier);
                    continue;
                }

                const int MaxDescriptionLength = 32_768; // 32KB cap
                var description = issue.Description.Length > MaxDescriptionLength
                    ? issue.Description[..MaxDescriptionLength] + "\n\n[... truncated ...]"
                    : issue.Description;
                var filePath = Path.Combine(issueContextDir, $"linked-issue-{safeId}.md");
                var content = $"# Issue #{issue.Identifier}: {issue.Title}\n\n{description}";
                await File.WriteAllTextAsync(filePath, content, ct);
            }

            context.Logger.Information("Wrote {Count} linked issue file(s) to workspace for PR #{PrNumber}",
                linkedIssues.Count, prNumber);
        }

        // --- Phase 2: Synthesize IssueDetail/ParsedIssue for ReviewCodeStep ---
        // ReviewCodeStep → BuildAgentPhaseContext → BuildReviewPrompt requires non-null Issue/ParsedIssue.
        // We synthesize from pre-fetched linked issues or PR metadata so ReviewCodeStep works unchanged.

        if (linkedIssues.Count > 0)
        {
            // Use the first linked issue as the primary context (agent sees all via files)
            var primary = linkedIssues[0];
            context.Issue = new IssueDetail
            {
                Identifier = primary.Identifier,
                Title = primary.Title,
                Description = primary.Description,
                Labels = Array.Empty<string>()
            };
            context.ParsedIssue = _issueParser.Parse(primary.Description);
        }
        else
        {
            // No linked issue found — synthesize from PR metadata
            context.Logger.Warning("No linked issue context for PR #{PrNumber}, synthesizing from PR metadata", prNumber);
            context.Issue = new IssueDetail
            {
                Identifier = prNumber.ToString(),
                Title = prTitle,
                Description = prDescription,
                Labels = Array.Empty<string>()
            };
            context.ParsedIssue = _issueParser.Parse(prDescription);
        }

        return StepResult.Continue;
    }
}
