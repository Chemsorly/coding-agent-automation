using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Syncs the brain repository and assembles the skills text for injection (spec 054, req 2.1-2.2, 5).
/// Non-fatal on failure.
/// </summary>
public sealed class SyncBrainPreRunStep : IPipelineStep
{
    public string StepName => "SyncBrainPreRun";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("SyncBrainPreRun");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        if (context.BrainProvider is null || context.BrainSync is null)
        {
            context.Logger.Information("SyncBrainPreRun skipped: BrainProvider={HasBrain}, BrainSync={HasSync}",
                context.BrainProvider is not null, context.BrainSync is not null);
            activity?.SetTag("pipeline.brain_sync.skipped", true);
            context.Run.BrainSkillsResult = BrainSkillsResults.NoBrain;
            return StepResult.Continue;
        }

        context.Callbacks.TransitionTo(PipelineStep.SyncingBrainRepoPreRun);
        await context.TryNonCriticalAsync(
            () => context.BrainSync.SyncPreRunAsync(
                context.Run, context.BrainProvider, context.Run.WorkspacePath!, ct, context.Callbacks.EmitOutputLine),
            "brain sync", ct,
            onFailure: () => context.Run.BrainContextLoaded = false);

        // ── Assemble skills (spec 054, req 5) ──────────────────────────────
        if (context.Run.BrainContextLoaded && context.Run.WorkspacePath is not null)
        {
            await context.TryNonCriticalAsync(
                () => AssembleSkillsAsync(context, ct),
                "brain skills assembly", ct);
        }
        else
        {
            context.Run.BrainSkillsResult = BrainSkillsResults.NoBrain;
        }

        activity?.SetTag("pipeline.brain.skills_result", context.Run.BrainSkillsResult ?? BrainSkillsResults.Unknown);
        activity?.SetTag("pipeline.brain.skills_bytes", context.Run.BrainSkillsBytes ?? 0);
        if (context.Run.BrainSkillsCommit is not null)
            activity?.SetTag("pipeline.brain.commit", context.Run.BrainSkillsCommit);
        activity?.SetTag("pipeline.brain.stack_labels", context.Run.StackLabels.Count);

        await context.Callbacks.ReportBrainSyncResult(
            context.Run.BrainContextLoaded,
            context.Run.BrainSkills?.FilesInjected.Count ?? context.Run.BrainKnowledgeFileCount);

        return StepResult.Continue;
    }

    private static async Task AssembleSkillsAsync(PipelineStepContext context, CancellationToken ct)
    {
        var brainPath = Path.Combine(context.Run.WorkspacePath!, ".brain");
        var run = context.Run;

        // Get the repository short name from the issue identifier (owner/repo#123 → repo)
        var repoShortName = ExtractShortName(run.IssueIdentifier);

        // Read all candidate skill files from the brain
        var stackLabels = run.StackLabels ?? [];
        var candidatePaths = new List<string> { BrainLayout.GeneralSkillPath };
        if (!string.IsNullOrEmpty(repoShortName))
            candidatePaths.Add(BrainLayout.RepositorySkillPath(repoShortName));
        foreach (var label in stackLabels)
        {
            var techPath = BrainLayout.TechnologySkillPath(label);
            if (techPath is not null) candidatePaths.Add(techPath);
        }

        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rel in candidatePaths)
        {
            var full = Path.Combine(brainPath, rel.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(full))
            {
                try
                {
                    var content = await File.ReadAllTextAsync(full, new System.Text.UTF8Encoding(false, true), ct);
                    files[rel] = content;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    context.Logger.Warning(ex, "Pipeline {RunId} could not read brain skill file {SkillFile}: {Error}",
                        run.RunId, rel, ex.Message);
                }
            }
        }

        var assembly = BrainSkillsAssembler.Assemble(files, repoShortName, stackLabels);

        // Get the brain commit SHA
        string? commitSha = null;
        try
        {
            commitSha = await context.BrainProvider!.GetHeadCommitShaAsync(brainPath, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Pipeline {RunId} could not read brain commit SHA: {Error}", run.RunId, ex.Message);
        }

        run.BrainSkillsResult = assembly.Result;
        run.BrainSkillsBytes = assembly.Bytes;
        run.BrainSkillsCommit = commitSha;
        run.BrainSkills = new BrainSkillsInjection(
            assembly.Text, assembly.Result, assembly.Bytes,
            assembly.FilesInjected, commitSha);

        if (assembly.FilesSkipped.Count > 0)
        {
            foreach (var skip in assembly.FilesSkipped)
                context.Logger.Warning("Pipeline {RunId} skipped brain skill file {SkillFile}: {Reason}",
                    run.RunId, skip.Path, skip.Reason);
        }

        context.Logger.Information(
            "Pipeline {RunId} brain skills assembled: result={BrainSkillsResult}, bytes={BrainSkillsBytes}, commit={BrainCommit}",
            run.RunId, assembly.Result, assembly.Bytes, commitSha ?? "none");
    }

    private static string? ExtractShortName(string? issueIdentifier)
    {
        if (string.IsNullOrEmpty(issueIdentifier)) return null;
        var hash = issueIdentifier.IndexOf('#');
        var name = hash > 0 ? issueIdentifier[..hash] : issueIdentifier;
        var slash = name.LastIndexOf('/');
        return slash >= 0 ? name[(slash + 1)..] : name;
    }
}
