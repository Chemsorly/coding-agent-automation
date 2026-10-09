using System.Diagnostics;
using System.Text;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Writes <c>.agent/rework-context.md</c> for a rework run whose rebase onto main force-resolved
/// conflicts. Main is authoritative, so the branch's changes to the conflicting files were
/// dropped. The file gives the rework agent, per file, the change its branch had made, main's
/// change since the branch point and main's commits, so it can re-apply what the issue still
/// needs instead of guessing (issue #3093).
/// Non-fatal: if writing fails, the pipeline continues without the file.
/// </summary>
public static class ReworkContextWriter
{
    /// <summary>Longest diff, in lines, written per change; the git command gives the rest.</summary>
    internal const int MaxDiffLines = 200;

    /// <summary>
    /// Writes the formatted context to <see cref="AgentWorkspacePaths.ReworkContextFilePath"/>.
    /// On any non-cancellation exception a warning is logged and the pipeline continues.
    /// </summary>
    public static async Task WriteAsync(PipelineStepContext context, MergeResult mergeResult, CancellationToken ct)
    {
        try
        {
            var contextDir = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.MetadataDirectory);
            Directory.CreateDirectory(contextDir);

            var filePath = Path.Combine(context.Run.WorkspacePath!, AgentWorkspacePaths.ReworkContextFilePath);
            await File.WriteAllTextAsync(filePath, Format(mergeResult), ct);

            context.Logger.Information(
                "Pipeline {RunId} wrote rework context for {FileCount} force-resolved file(s) to {FilePath}",
                context.Run.RunId, mergeResult.ConflictFiles.Count, AgentWorkspacePaths.ReworkContextFilePath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Activity.Current?.RecordError(ex, ct);
            context.Logger.Warning(ex,
                "Pipeline {RunId} failed to write rework context, rework will proceed without it", context.Run.RunId);
        }
    }

    /// <summary>Formats the rework context for a force-resolved rebase as markdown.</summary>
    public static string Format(MergeResult mergeResult)
    {
        ArgumentNullException.ThrowIfNull(mergeResult);

        var sb = new StringBuilder();
        sb.AppendLine("# Rework context: changes dropped by the rebase onto main");
        sb.AppendLine();
        sb.AppendLine("The pipeline rebased this pull request's branch onto the latest main. " +
            "Main is authoritative: where your branch and main changed the same file, main's version was kept " +
            "and your branch's conflicting changes to that file were dropped. Changes from your other commits " +
            "that applied cleanly may still be in it. Where your branch had renamed a file main changed, the " +
            "renamed copy was removed and both names are listed.");
        sb.AppendLine();
        sb.AppendLine("For each file below you get the change your branch had made (dropped), main's change since " +
            "your branch point (kept), and main's commits that touched the file. Use them to decide what to " +
            "re-apply on top of main's version:");
        sb.AppendLine();
        sb.AppendLine($"- Re-apply only what this issue still needs (see `{AgentWorkspacePaths.IssueContextFilePath}`).");
        sb.AppendLine("- Do not re-apply a change that is outside this issue's scope.");
        sb.AppendLine("- Do not re-apply a change that main has since made in another way. Keep main's way.");
        sb.AppendLine("- Do not restore a file, or part of one, from your branch's earlier commits or its `origin/` " +
            "copy, and do not undo the diffs below in bulk: that reverts main's changes. Edit the files as they " +
            "are now.");
        sb.AppendLine();

        // Truncation header (AC #3): list files whose BranchChange diffs are truncated at the top
        // so the agent knows which files need the full diff read via git before re-applying.
        // TODO: ForceResolvedContext is not deduplicated here (unlike the contexts dict below which
        // uses DistinctBy). If the same path appears twice in ForceResolvedContext it will be listed
        // twice in the truncation header while appearing only once in the file sections below —
        // apply DistinctBy(c => c.Path) before the Where clause to match the dedup behaviour below.
        var truncatedFiles = mergeResult.ForceResolvedContext
            .Where(c => c.BranchChange.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length > MaxDiffLines)
            .ToList();
        if (truncatedFiles.Count > 0)
        {
            sb.AppendLine("## ⚠️ Truncated diffs");
            sb.AppendLine();
            sb.AppendLine("The following files have branch diffs that exceeded the display limit and were truncated. " +
                "Run the git command shown above each truncated section to read the full diff before re-applying:");
            sb.AppendLine();
            foreach (var tc in truncatedFiles)
            {
                // TODO: totalLines is computed by re-normalising and splitting the same string that was
                // already computed in the Where clause above. Collapse into a single pass by capturing
                // the line count during the Where evaluation (e.g. via .Select) to avoid redundant
                // string allocations when multiple large diffs are present.
                var totalLines = tc.BranchChange.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length;
                sb.AppendLine($"- `{tc.Path}` — {totalLines} lines total, first {MaxDiffLines} shown");
            }
            sb.AppendLine();
        }

        var contexts = mergeResult.ForceResolvedContext
            .DistinctBy(c => c.Path, StringComparer.Ordinal)
            .ToDictionary(c => c.Path, StringComparer.Ordinal);
        foreach (var path in mergeResult.ConflictFiles)
        {
            sb.AppendLine($"## `{path}`");
            sb.AppendLine();

            if (!contexts.TryGetValue(path, out var context))
            {
                sb.AppendLine("No diff is available for this file. Compare it with your pull request's version.");
                sb.AppendLine();
                continue;
            }

            if (context.BaseCommits.Count > 0)
            {
                sb.AppendLine("Main's commits that touched this file since your branch point:");
                sb.AppendLine();
                foreach (var commit in context.BaseCommits)
                    sb.AppendLine($"- {commit}");
                sb.AppendLine();
            }

            AppendDiff(sb, "Your branch's change (dropped)", context.BranchChange,
                GitDiffCommand(mergeResult.MergeBaseSha, mergeResult.PreviousHeadSha, path));
            AppendDiff(sb, "Main's change (kept)", context.BaseChange,
                GitDiffCommand(mergeResult.MergeBaseSha, mergeResult.BaseHeadSha, path));
        }

        return sb.ToString().TrimEnd() + Environment.NewLine;
    }

    private static string? GitDiffCommand(string? from, string? to, string path) =>
        from is null || to is null ? null : $"git diff {from} {to} -- {path}";

    private static void AppendDiff(StringBuilder sb, string heading, string diff, string? fullDiffCommand)
    {
        sb.AppendLine($"### {heading}");
        sb.AppendLine();
        if (fullDiffCommand is not null)
        {
            sb.AppendLine($"Full diff: `{fullDiffCommand}`");
            sb.AppendLine();
        }

        var lines = diff.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n');
        // Four backticks, so a diff of a markdown file cannot close the fence.
        sb.AppendLine("````diff");
        foreach (var line in lines.Take(MaxDiffLines))
            sb.AppendLine(line);
        sb.AppendLine("````");
        if (lines.Length > MaxDiffLines)
            sb.AppendLine($"… {lines.Length - MaxDiffLines} more lines; run the full diff command above.");
        sb.AppendLine();
    }
}
