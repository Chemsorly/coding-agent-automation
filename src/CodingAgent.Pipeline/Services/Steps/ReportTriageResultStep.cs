using System.Diagnostics;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Reports a triage run's validated result to the API, which records it on the run's triage. For a tracker
/// issue it then posts (or updates, by marker) the root cause analysis comment and sets
/// <c>agent:triage-review</c>. Reporting and posting are critical: a person must never be asked to review a
/// result that was not recorded or not shown. The label swap is not: the run's final label is set again on
/// completion.
/// </summary>
public sealed class ReportTriageResultStep : IPipelineStep
{
    private readonly bool _reportToTracker;

    /// <param name="reportToTracker">True when the result is posted on a tracker issue.</param>
    public ReportTriageResultStep(bool reportToTracker)
    {
        _reportToTracker = reportToTracker;
    }

    public string StepName => "ReportTriageResult";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ReportTriageResult");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        context.Callbacks.TransitionTo(PipelineStep.ReportingRca);

        var resultPath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.TriageResultFilePath);
        var parsed = await TriageInvestigationStep.ReadResultAsync(resultPath, ct);
        if (parsed.Result is null)
        {
            await context.FailRunAsync(parsed.Error!, ct);
            return StepResult.Stop;
        }

        var json = await File.ReadAllTextAsync(resultPath, ct);
        var reported = await context.TryCriticalAsync(
            () => context.IssueOps.ReportTriageResultAsync(json, ct), "Report the triage result", ct);
        if (reported == StepResult.Stop)
            return StepResult.Stop;
        context.Callbacks.EmitOutputLine("📤 Reported the triage result");

        if (!_reportToTracker)
            return StepResult.Continue;

        var comment = TriageCommentRenderer.Render(parsed.Result);
        if (context.InjectedSecrets is { Count: > 0 } secrets)
            comment = SecretMasker.Mask(comment, secrets);

        var posted = await context.TryCriticalAsync(async () =>
        {
            var comments = await context.IssueOps.ListCommentsAsync(context.Run.IssueIdentifier, ct);
            var existing = comments.LastOrDefault(c => c.Body.Contains(TriageConstants.CommentMarker, StringComparison.Ordinal));
            if (existing is not null && long.TryParse(existing.Id, out var commentId))
                await context.IssueOps.UpdateCommentAsync(context.Run.IssueIdentifier, commentId, comment, ct);
            else
                await context.IssueOps.PostCommentAsync(context.Run.IssueIdentifier, comment, ct);
        }, "Post the root cause analysis comment", ct);
        if (posted == StepResult.Stop)
            return StepResult.Stop;
        context.Callbacks.EmitOutputLine("💬 Posted the root cause analysis on the issue");

        context.Run.FinalLabel = AgentLabels.TriageReview;
        try
        {
            await context.IssueOps.SwapLabelAsync(context.Run.IssueIdentifier, AgentLabels.TriageReview, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex, ct);
            context.Logger.Error(ex,
                "Failed to swap label to {Label} on issue {IssueId}; the completion sets it again",
                AgentLabels.TriageReview, context.Run.IssueIdentifier);
        }

        return StepResult.Continue;
    }
}
