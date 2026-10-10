using System.Text;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Writes a triage run's context: the report (<see cref="AgentWorkspacePaths.IssueContextFilePath"/>, from the
/// issue or the operator's form, which the assignment carries) and the triage context
/// (<see cref="AgentWorkspacePaths.TriageContextFilePath"/>: earlier attempts and other triages, rendered by the
/// API). Both are critical: the investigation reads them. Then it downloads the open issues of every tracker of
/// the project into <see cref="AgentWorkspacePaths.OpenIssuesDirectory"/>, which is only enrichment.
/// </summary>
public sealed class WriteTriageContextStep : IPipelineStep
{
    private const int PageSize = 50;
    private const int MaxDescriptionChars = 2_000;

    private readonly string? _triageContextMarkdown;

    /// <param name="triageContextMarkdown">The assignment's <see cref="JobAssignmentMessage.TriageContextMarkdown"/>.</param>
    public WriteTriageContextStep(string? triageContextMarkdown)
    {
        _triageContextMarkdown = triageContextMarkdown;
    }

    public string StepName => "WriteTriageContext";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("WriteTriageContext");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        context.Callbacks.TransitionTo(PipelineStep.DownloadingOpenIssues);

        if (context.Issue is null)
        {
            await context.FailRunAsync("The triage report is missing from the job assignment", FailureReason.InfrastructureFailure, ct);
            return StepResult.Stop;
        }

        var workspace = context.Run.WorkspacePath!;
        var written = await context.TryCriticalAsync(async () =>
        {
            Directory.CreateDirectory(Path.Combine(workspace, AgentWorkspacePaths.MetadataDirectory));

            var comments = (context.IssueComments ?? [])
                .OrderBy(c => c.CreatedAt)
                .TakeLast(TriageConstants.MaxCommentsForContext)
                .ToList();
            var parsed = context.ParsedIssue ?? new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" };
            var report = PromptBuilder.BuildIssueContextFileContent(context.Issue, parsed, comments, context.DownloadedImages);
            await File.WriteAllTextAsync(Path.Combine(workspace, AgentWorkspacePaths.IssueContextFilePath), report, ct);

            var triageContext = string.IsNullOrWhiteSpace(_triageContextMarkdown)
                ? "# Triage Context\n\nThis is the first attempt, and the project has no other recent triages.\n"
                : _triageContextMarkdown;
            await File.WriteAllTextAsync(Path.Combine(workspace, AgentWorkspacePaths.TriageContextFilePath), triageContext, ct);
        }, "Write the triage report", ct);

        if (written == StepResult.Stop)
            return StepResult.Stop;

        context.Callbacks.EmitOutputLine("📋 Wrote the triage report and context");

        var count = await DownloadOpenIssuesAsync(context, workspace, ct);
        context.Run.OpenIssuesDownloaded = count;
        context.Callbacks.EmitOutputLine($"📥 Downloaded {count} open issues of the project's trackers");
        return StepResult.Continue;
    }

    /// <summary>
    /// Lists the open issues of every tracker of the project. A tracker that fails is skipped: the open issues
    /// only help the agent find duplicates.
    /// </summary>
    private static async Task<int> DownloadOpenIssuesAsync(PipelineStepContext context, string workspace, CancellationToken ct)
    {
        var trackers = (context.ProjectContext?.Repositories ?? [])
            .Where(r => !string.IsNullOrEmpty(r.IssueProviderId))
            .GroupBy(r => r.IssueProviderId!, StringComparer.Ordinal)
            .Select(g => (TrackerId: g.Key, Name: g.First().TemplateName))
            .ToList();

        var outputDir = Path.Combine(workspace, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.CreateDirectory(outputDir);

        var maxPerTracker = Math.Max(1, context.Config.MaxOpenIssuesForContext);
        var written = 0;
        foreach (var (trackerId, name) in trackers)
        {
            try
            {
                var issues = new List<IssueSummary>();
                for (var page = 1; issues.Count < maxPerTracker; page++)
                {
                    var result = await context.IssueOps.ListOpenIssuesForProviderAsync(trackerId, page, PageSize, null, ct);
                    issues.AddRange(result.Items);
                    if (!result.HasMore || result.Items.Count == 0)
                        break;
                }

                foreach (var issue in issues.Take(maxPerTracker))
                {
                    // The triaged issue itself is the report, not an existing issue
                    if (issue.Url is not null && issue.Url == context.Issue?.Url)
                        continue;

                    var fileName = $"{SafeName(name)}-{SafeName(issue.Identifier)}.md";
                    await File.WriteAllTextAsync(Path.Combine(outputDir, fileName), FormatIssue(name, issue), ct);
                    written++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.Warning(ex, "Pipeline {RunId}: could not list the open issues of {Repository}'s tracker; continuing without them",
                    context.Run.RunId, name);
                context.Callbacks.EmitOutputLine($"⚠️ Could not list the open issues of {name}");
            }
        }

        return written;
    }

    private static string FormatIssue(string repository, IssueSummary issue)
    {
        var sb = new StringBuilder();
        sb.AppendLine("---");
        sb.AppendLine($"repository: {repository}");
        sb.AppendLine($"identifier: {issue.Identifier}");
        if (issue.Url is not null)
            sb.AppendLine($"url: {issue.Url}");
        if (issue.Labels.Count > 0)
            sb.AppendLine($"labels: [{string.Join(", ", issue.Labels)}]");
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine($"# {issue.Title}");
        if (!string.IsNullOrWhiteSpace(issue.Description))
        {
            sb.AppendLine();
            sb.AppendLine(issue.Description.Length > MaxDescriptionChars
                ? issue.Description[..MaxDescriptionChars] + "\n…"
                : issue.Description);
        }
        return sb.ToString();
    }

    private static string SafeName(string value)
    {
        var chars = value.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray();
        var name = new string(chars).Trim('_');
        return name.Length == 0 ? "x" : name;
    }
}
