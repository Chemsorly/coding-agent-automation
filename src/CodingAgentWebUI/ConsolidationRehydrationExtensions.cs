using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

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
        ArgumentNullException.ThrowIfNull(pipelineConfig);

        // Clean up orphaned consolidation runs from previous sessions.
        // A run is only truly orphaned if no agent is currently working on it.
        // Since agents connect to the API hub (not the orchestrator), a consolidation
        // run with Status=Running may still have an active agent after an orchestrator
        // restart. Query the API directly — IAgentRegistryService is backed by a polling
        // snapshot that has NOT yet fired at startup time (AgentRegistrySyncService is a
        // BackgroundService that starts after app.Run), so using GetAllAgents() would always
        // return empty and the skip guard would never fire.
        var consolidationService = app.Services.GetRequiredService<IConsolidationService>();
        var apiAgentClient = app.Services.GetRequiredService<IPipelineApiAgentClient>();
        IReadOnlyList<AgentEntry> liveAgents;
        try
        {
            liveAgents = await apiAgentClient.GetAgentsAsync(CancellationToken.None);
        }
        catch
        {
            // If the API is unreachable (e.g. cold start), treat all running runs as orphaned.
            liveAgents = [];
        }
        var activeAgentJobIds = new HashSet<string>(
            liveAgents.Where(a => a.ActiveJobId != null).Select(a => a.ActiveJobId!),
            StringComparer.OrdinalIgnoreCase);
        await consolidationService.CleanupOrphanedRunsAsync(activeAgentJobIds, CancellationToken.None);

        // Load live config from the store (the startup singleton may be stale in DB mode)
        var configStore = app.Services.GetRequiredService<IPipelineConfigStore>();
        var liveConfig = await configStore.LoadPipelineConfigAsync(CancellationToken.None);

        // Rehydrate queued consolidation runs via IWorkDistributor (unified dispatch path)
        var queuedRuns = await consolidationService.RehydrateQueuedRunsAsync(CancellationToken.None);
        if (queuedRuns.Count > 0)
        {
            var workDistributor = app.Services.GetRequiredService<IWorkDistributor>();
            var profileStore = app.Services.GetRequiredService<IAgentProfileStore>();
            var workspaceManager = app.Services.GetRequiredService<IConsolidationWorkspaceManager>();
            var rehydrationProfiles = await profileStore.LoadAgentProfilesAsync(CancellationToken.None);
            foreach (var run in queuedRuns)
            {
                // Resolve full profile MatchLabels from QueuedRequiredLabels to produce correct AgentSelector
                var requiredLabels = run.QueuedRequiredLabels ?? [];
                var profile = ProfileResolver.ResolveByRequiredLabels(rehydrationProfiles, requiredLabels.ToList());
                var selectorLabels = profile?.MatchLabels ?? requiredLabels;

                var request = new JobDistributionRequest
                {
                    IssueIdentifier = run.RunId,
                    IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                    RepoProviderConfigId = "",
                    InitiatedBy = ConsolidationConstants.InitiatedBy,
                    TaskType = WorkItemTaskType.Consolidation,
                    AgentSelector = AgentSelectorKey.From(selectorLabels),
                    TimeoutSeconds = (int)liveConfig.AgentTimeout.TotalSeconds,
                    ConsolidationRunType = run.Type,
                    ConsolidationTemplateId = run.TemplateId,
                    ConsolidationWorkspacePath = workspaceManager.GetWorkspacePath(run.RunId),
                    RunId = run.RunId,
                    AutoDispatch = run.AutoDispatch,
                    // Carry the traceparent stored at trigger time so the resulting WorkItem
                    // inherits the original trace even though Activity.Current is null here
                    // (startup runs outside any HTTP request context).
                    TraceContext = !string.IsNullOrEmpty(run.TraceParent)
                        ? new Dictionary<string, string> { ["traceparent"] = run.TraceParent }
                        : null
                };
                await workDistributor.DistributeAsync(request, CancellationToken.None);
            }
        }
    }
}
