using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for post-build application startup tasks: consolidation cleanup,
/// run rehydration, and pipeline loop auto-start.
/// </summary>
internal static class StartupTasks
{
    /// <summary>
    /// Runs post-build startup tasks: cleans up orphaned consolidation runs, rehydrates
    /// queued runs, and auto-starts the pipeline loop if configured.
    /// </summary>
    public static async Task RunPostBuildStartupAsync(this WebApplication app, PipelineConfiguration pipelineConfig)
    {
        // Clean up orphaned consolidation runs from previous sessions
        var consolidationService = app.Services.GetRequiredService<IConsolidationService>();
        await consolidationService.CleanupOrphanedRunsAsync(CancellationToken.None);

        // Rehydrate queued consolidation runs via IWorkDistributor (unified dispatch path)
        var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
        if (queuedRuns.Count > 0)
        {
            var workDistributor = app.Services.GetRequiredService<IWorkDistributor>();
            var profileStore = app.Services.GetRequiredService<IAgentProfileStore>();
            var profileResolver = new ProfileResolver();
            var rehydrationProfiles = await profileStore.LoadAgentProfilesAsync(CancellationToken.None);
            foreach (var run in queuedRuns)
            {
                // Resolve full profile MatchLabels from QueuedRequiredLabels to produce correct AgentSelector
                var requiredLabels = run.QueuedRequiredLabels ?? [];
                var profile = profileResolver.ResolveByRequiredLabels(rehydrationProfiles, requiredLabels.ToList());
                var selectorLabels = profile?.MatchLabels ?? requiredLabels;

                var request = new JobDistributionRequest
                {
                    IssueIdentifier = run.RunId,
                    IssueProviderConfigId = "consolidation",
                    RepoProviderConfigId = "",
                    InitiatedBy = "consolidation",
                    TaskType = WorkItemTaskType.Consolidation,
                    AgentSelector = string.Join(",", selectorLabels.OrderBy(l => l, StringComparer.Ordinal)),
                    TimeoutSeconds = 0,
                    ConsolidationRunType = run.Type,
                    ConsolidationTemplateId = run.TemplateId,
                    ConsolidationWorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
                    RunId = run.RunId
                };
                await workDistributor.DistributeAsync(request, CancellationToken.None);
            }
        }

        // Auto-start pipeline loop if configured
        if (pipelineConfig.ClosedLoopAutoStart)
        {
            var loopService = app.Services.GetRequiredService<PipelineLoopService>();
            var loopStarted = await loopService.StartLoopAsync();
            if (loopStarted)
                Log.Information("Pipeline loop auto-started (ClosedLoopAutoStart=true)");
            else
                Log.Warning("Pipeline loop auto-start requested but StartLoopAsync returned false (no valid templates?)");
        }
    }
}
