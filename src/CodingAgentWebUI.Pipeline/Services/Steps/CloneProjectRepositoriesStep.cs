using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

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
internal sealed class CloneProjectRepositoriesStep : IPipelineStep
{
    /// <summary>Maximum concurrent repo clones.</summary>
    private const int MaxParallelClones = 3;

    /// <summary>Per-repo clone timeout.</summary>
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromSeconds(120);

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        // Skip if not cross-repo decomposition
        if (context.ProjectContext is null || context.AdditionalRepoProviders is null || context.AdditionalRepoProviders.Count == 0)
            return StepResult.Continue;

        var workspacePath = context.Run.WorkspacePath!;
        var reposDir = Path.Combine(workspacePath, "repos");
        Directory.CreateDirectory(reposDir);

        context.Callbacks.EmitOutputLine($"📦 Cloning {context.AdditionalRepoProviders.Count} additional project repo(s)...");

        // Clone in parallel with concurrency cap
        using var semaphore = new SemaphoreSlim(MaxParallelClones);
        var tasks = new List<Task>();

        foreach (var (templateName, provider) in context.AdditionalRepoProviders)
        {
            var cloneTask = CloneRepoAsync(templateName, provider, reposDir, context, semaphore, ct);
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
        IRepositoryProvider provider,
        string reposDir,
        PipelineStepContext context,
        SemaphoreSlim semaphore,
        CancellationToken ct)
    {
        await semaphore.WaitAsync(ct);
        try
        {
            var targetDir = Path.Combine(reposDir, templateName);
            Directory.CreateDirectory(targetDir);

            using var timeoutCts = new CancellationTokenSource(CloneTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

            try
            {
                await provider.CloneAsync(targetDir, linkedCts.Token);

                // Mark the repo as cloned by setting LocalPath on the matching RepositoryTarget
                var target = context.ProjectContext!.Repositories.FirstOrDefault(
                    r => string.Equals(r.TemplateName, templateName, StringComparison.Ordinal));
                if (target is not null)
                    target.LocalPath = $"repos/{templateName}";

                context.Logger.Information("Cloned additional repo '{TemplateName}' to repos/{TemplateName}",
                    templateName, templateName);
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
                context.Logger.Warning(ex, "Failed to clone additional repo '{TemplateName}' — marking unavailable: {Error}",
                    templateName, ex.Message);
            }
        }
        finally
        {
            semaphore.Release();
        }
    }
}
