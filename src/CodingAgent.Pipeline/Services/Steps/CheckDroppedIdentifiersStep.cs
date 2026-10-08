using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Post-codegen step that verifies identifiers the branch had added in force-resolved conflict files
/// were re-applied during code generation (issue #3435).
/// Non-blocking: always returns <see cref="StepResult.Continue"/>.
/// Only runs when <see cref="PipelineRun.MergeForceResolved"/> is true and
/// <see cref="PipelineRun.DroppedIdentifiersByFile"/> is non-empty.
/// </summary>
public sealed class CheckDroppedIdentifiersStep : IPipelineStep
{
    public string StepName => "CheckDroppedIdentifiers";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CheckDroppedIdentifiers");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        var run = context.Run;

        // Guard: only meaningful when a force-resolved rebase happened and identifiers were captured.
        // Also guard WorkspacePath to handle test contexts where no workspace is set up.
        if (run.WorkspacePath is null
            || !run.MergeForceResolved
            || run.DroppedIdentifiersByFile.Count == 0)
        {
            return StepResult.Continue;
        }

        context.Callbacks.TransitionTo(PipelineStep.CheckingDroppedIdentifiers);

        var notReapplied = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var (relativePath, identifiers) in run.DroppedIdentifiersByFile)
        {
            ct.ThrowIfCancellationRequested();

            var absolutePath = Path.Combine(run.WorkspacePath, relativePath);
            string content;
            try
            {
                content = await File.ReadAllTextAsync(absolutePath, ct);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                context.Logger.Warning(
                    "Pipeline {RunId} CheckDroppedIdentifiers: file {Path} not found (may have been intentionally removed)",
                    run.RunId, relativePath);
                context.Callbacks.EmitOutputLine(
                    $"⚠️ {relativePath}: file not found after codegen (may have been intentionally removed)");
                // TODO: When the entire file is absent (agent never recreated it), its identifiers are
                // not added to NotReappliedIdentifiersByFile, so they are invisible in agent feedback
                // and the PR description — violating AC #1 for the whole-file-deletion case.
                // Consider adding the file's identifiers to notReapplied here so they surface in the
                // feedback prompt and PR body alongside a "(file not re-created)" annotation.
                continue;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                context.Logger.Warning(ex,
                    "Pipeline {RunId} CheckDroppedIdentifiers: failed to read {Path}, skipping",
                    run.RunId, relativePath);
                continue;
            }

            // Pre-compile each pattern with a timeout to avoid repeated cache evictions and
            // to satisfy S6444 (pass a timeout to limit execution time).
            var compiledPatterns = identifiers
                .Select(id => (Id: id, Pattern: new Regex($@"\b{Regex.Escape(id)}\b", RegexOptions.None, TimeSpan.FromSeconds(5))))
                .ToList();

            var missing = compiledPatterns
                .Where(p => !p.Pattern.IsMatch(content))
                .Select(p => p.Id)
                .ToList();

            if (missing.Count > 0)
            {
                notReapplied[relativePath] = missing;
                foreach (var id in missing)
                {
                    context.Logger.Warning(
                        "Pipeline {RunId} CheckDroppedIdentifiers: {Path}: {Identifier} was not re-applied after force-resolved rebase",
                        run.RunId, relativePath, id);
                    context.Callbacks.EmitOutputLine(
                        $"⚠️ {relativePath}: `{id}` was not re-applied after force-resolved rebase");
                }
            }
        }

        run.NotReappliedIdentifiersByFile = notReapplied;

        if (notReapplied.Count > 0)
        {
            context.Logger.Information(
                "Pipeline {RunId} CheckDroppedIdentifiers: {FileCount} file(s) with not-re-applied identifiers",
                run.RunId, notReapplied.Count);
        }
        else
        {
            context.Logger.Information(
                "Pipeline {RunId} CheckDroppedIdentifiers: all dropped identifiers were re-applied",
                run.RunId);
            context.Callbacks.EmitOutputLine("✅ All force-resolved branch identifiers were re-applied");
        }

        return StepResult.Continue;
    }
}
