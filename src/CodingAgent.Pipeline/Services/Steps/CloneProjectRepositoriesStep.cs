using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Clones additional project repositories into subdirectories for cross-repo decomposition.
/// Runs after <see cref="CloneRepositoryStep"/> (primary repo already at workspace root).
/// Skips gracefully when:
/// - No <see cref="PipelineStepContext.ProjectContext"/> is present (per-template decomposition)
/// - No <see cref="PipelineStepContext.AdditionalRepoProviders"/> are configured
///
/// Each additional repo is cloned into <c>{workspace}/repos/{template-name}/</c>.
/// Clone failures are non-critical: the repo is marked unavailable via <see cref="RepositoryTarget.LocalPath"/>
/// remaining null, and a warning is logged. The pipeline continues with whatever repos are available.
///
/// Clones run in parallel (up to 3 concurrent) to minimize startup latency.
/// A per-repo timeout of 120 seconds prevents one slow clone from blocking the pipeline.
/// </summary>
public sealed class CloneProjectRepositoriesStep : IPipelineStep
{
    public string StepName => "CloneProjectRepositories";

    /// <summary>Maximum concurrent repo clones.</summary>
    private const int MaxParallelClones = 3;

    /// <summary>Per-repo clone timeout.</summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(120);

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CloneProjectRepositories");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("pipeline.run_type", context.Run.RunType.ToString());
        PipelineTelemetry.SetProjectTags(activity, context.Run.ProjectId, context.Run.ProjectName);

        // Skip if not cross-repo decomposition
        if (context.ProjectContext is null || context.AdditionalRepoProviders is null || context.AdditionalRepoProviders.Count == 0)
            return StepResult.Continue;

        var workspacePath = context.Run.WorkspacePath!;
        var reposDir = Path.Combine(workspacePath, "repos");
        Directory.CreateDirectory(reposDir);

        context.Callbacks.EmitOutputLine($"📦 Cloning {context.AdditionalRepoProviders.Count} additional project repo(s)...");

        // Clone in parallel with concurrency cap. Folder names are assigned up front, because two
        // template names can map to the same folder name (for example "web app" and "web_app").
        using var semaphore = new SemaphoreSlim(MaxParallelClones);
        var tasks = new List<Task>();
        var usedFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (templateName, provider) in context.AdditionalRepoProviders)
        {
            var folderName = UniqueFolderName(ToFolderName(templateName), usedFolderNames);
            var cloneTask = CloneRepoAsync(templateName, folderName, provider, reposDir, context, semaphore, ct);
            tasks.Add(cloneTask);
        }

        await Task.WhenAll(tasks);

        // Report results
        var clonedCount = context.ProjectContext.Repositories.Count(r => r.LocalPath is not null);
        var failedCount = context.AdditionalRepoProviders.Count - clonedCount;

        if (failedCount > 0)
            context.Callbacks.EmitOutputLine($"⚠️ {failedCount} repo(s) failed to clone — marked unavailable");
        else
            context.Callbacks.EmitOutputLine($"✅ All {clonedCount} additional repo(s) cloned successfully");

        return StepResult.Continue;
    }

    private static async Task CloneRepoAsync(
        string templateName,
        string folderName,
        IRepositoryProvider provider,
        string reposDir,
        PipelineStepContext context,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var targetDir = Path.Combine(reposDir, folderName);

            using var timeoutCts = new CancellationTokenSource(CloneTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                Directory.CreateDirectory(targetDir);
                await provider.CloneAsync(targetDir, linkedCts.Token);

                // Mark the repo as cloned by setting LocalPath on the matching RepositoryTarget
                var target = context.ProjectContext!.Repositories.FirstOrDefault(
                    r => string.Equals(r.TemplateName, templateName, StringComparison.Ordinal));
                if (target is not null)
                    target.LocalPath = $"repos/{folderName}";

                context.Logger.Information("Cloned additional repo '{TemplateName}' to '{TargetPath}'",
                    templateName, $"repos/{folderName}");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                context.Logger.Warning("Clone of repo '{TemplateName}' timed out after {Timeout}s — marking unavailable",
                    templateName, CloneTimeout.TotalSeconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // Propagate pipeline-level cancellation
            }
            catch (Exception ex)
            {
                Activity.Current?.RecordError(ex, ct);
                context.Logger.Warning(ex, "Failed to clone additional repo '{TemplateName}' — marking unavailable: {Error}",
                    templateName, ex.Message);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }

    private const int MaxFolderNameLength = 100;

    /// <summary>
    /// The folder a project repository is cloned into, from its template name: every character other
    /// than an ASCII letter, a digit, '-', '_' or '.' becomes '_', and leading or trailing dots are
    /// removed. The result is always a single folder inside <c>repos/</c> (no separators, no "..").
    /// </summary>
    internal static string ToFolderName(string templateName)
    {
        var safe = new string(templateName
                .Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_')
                .Take(MaxFolderNameLength)
                .ToArray())
            .Trim('.');

        return safe.Length == 0 ? "_" : safe;
    }

    /// <summary>
    /// <paramref name="folderName"/>, or the first of <c>folderName_2</c>, <c>folderName_3</c>, … that is
    /// not in <paramref name="usedFolderNames"/>; the result is added to the set.
    /// </summary>
    internal static string UniqueFolderName(string folderName, HashSet<string> usedFolderNames)
    {
        var candidate = folderName;
        for (var suffix = 2; !usedFolderNames.Add(candidate); suffix++)
            candidate = $"{folderName}_{suffix}";
        return candidate;
    }
}
