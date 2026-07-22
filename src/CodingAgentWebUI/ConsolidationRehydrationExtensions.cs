using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for cleaning up orphaned consolidation runs and rehydrating
/// queued consolidation runs at application startup.
/// </summary>
internal static class ConsolidationRehydrationExtensions
{
    /// <summary>
    /// Cleans up orphaned consolidation runs from previous sessions and rehydrates
    /// queued consolidation runs via <see cref="IWorkDistributor"/> (unified dispatch path).
    /// </summary>
    /// <remarks>
    /// Must run after <see cref="EndpointRegistration.MapApplicationEndpoints"/> so that
    /// middleware is configured before background work begins.
    /// </remarks>
    public static async Task RunConsolidationStartupAsync(this WebApplication app, PipelineConfiguration pipelineConfig)
    {
        ArgumentNullException.ThrowIfNull(app);
        // TODO: Add ArgumentNullException.ThrowIfNull(pipelineConfig) — pipelineConfig is dereferenced
        // on AgentTimeout and WorkspaceBaseDirectory but has no null guard (review-findings)

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
                    IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                    RepoProviderConfigId = "",
                    InitiatedBy = ConsolidationConstants.InitiatedBy,
                    TaskType = WorkItemTaskType.Consolidation,
                    AgentSelector = string.Join(",", selectorLabels.OrderBy(l => l, StringComparer.Ordinal)),
                    TimeoutSeconds = (int)pipelineConfig.AgentTimeout.TotalSeconds,
                    ConsolidationRunType = run.Type,
                    ConsolidationTemplateId = run.TemplateId,
                    ConsolidationWorkspacePath = Path.Combine(pipelineConfig.WorkspaceBaseDirectory, "consolidation", run.RunId),
                    RunId = run.RunId,
                    AutoDispatch = run.AutoDispatch
                };
                await workDistributor.DistributeAsync(request, CancellationToken.None);
            }
        }
    }
}
