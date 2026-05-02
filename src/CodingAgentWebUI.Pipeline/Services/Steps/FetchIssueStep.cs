using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Fetches the issue from the provider, validates it, parses the description,
/// and fetches issue comments. Skipped on the agent side where issue data is pre-populated.
/// </summary>
internal sealed class FetchIssueStep : IPipelineStep
{
    private readonly IssueDescriptionParser _issueParser;

    public FetchIssueStep(IssueDescriptionParser issueParser)
    {
        ArgumentNullException.ThrowIfNull(issueParser);
        _issueParser = issueParser;
    }

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var issueProvider = context.IssueProvider
            ?? throw new InvalidOperationException("FetchIssueStep requires an IssueProvider on the context.");

        IssueDetail issue;
        try { issue = await issueProvider.GetIssueAsync(context.Run.IssueIdentifier, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Error(ex, "Pipeline {RunId} failed to fetch issue {IssueIdentifier}",
                context.Run.RunId, context.Run.IssueIdentifier);
            await context.FailRunAsync($"Failed to fetch issue: {ex.Message}");
            return StepResult.Stop;
        }

        if (string.IsNullOrWhiteSpace(issue.Title) || string.IsNullOrWhiteSpace(issue.Description))
        {
            context.Logger.Warning("Pipeline {RunId} issue has insufficient information", context.Run.RunId);
            await context.FailRunAsync("insufficient issue information");
            return StepResult.Stop;
        }

        context.Run.IssueTitle = issue.Title;
        context.Run.IssueLabels = issue.Labels;
        var parsed = _issueParser.Parse(issue.Description);
        context.Issue = issue;
        context.ParsedIssue = parsed;

        context.EmitOutputLine($"🚀 Pipeline started for issue #{context.Run.IssueIdentifier} — {issue.Title}");

        IReadOnlyList<IssueComment> issueComments = Array.Empty<IssueComment>();
        try
        {
            issueComments = await issueProvider.ListCommentsAsync(context.Run.IssueIdentifier, ct);
            context.Logger.Information("Pipeline {RunId} fetched {CommentCount} comment(s) for issue {IssueIdentifier}",
                context.Run.RunId, issueComments.Count, context.Run.IssueIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Pipeline {RunId} failed to fetch issue comments, proceeding without them", context.Run.RunId);
        }
        context.IssueComments = issueComments;

        return StepResult.Continue;
    }
}
