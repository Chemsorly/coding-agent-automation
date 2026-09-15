using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for cleaning up orphaned consolidation runs at application startup.
/// </summary>
internal static class ConsolidationRehydrationExtensions
{
    /// <summary>
    /// Cleans up orphaned consolidation runs from previous sessions.
    /// </summary>
    /// <remarks>
    /// Pending runs are now poller-owned: consolidation WorkItems are created as
    /// <c>Pending</c> via <c>POST /api/work-items</c> and claimed by the
    /// <c>WorkItemDispatchPoller</c>. Queued runs not yet dispatched before a pod restart
    /// will be picked up by <see cref="Services.ConsolidationRetryBackgroundService"/>
    /// on its first sweep (default 2-minute interval) — no startup rehydration needed.
    /// <para>
    /// Must run after endpoint registration so that middleware is configured before
    /// background work begins.
    /// </para>
    /// </remarks>
    public static async Task RunConsolidationStartupAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

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
        IReadOnlyList<AgentEntryDto> liveAgentDtos;
        try
        {
            liveAgentDtos = await apiAgentClient.GetAgentsAsync(CancellationToken.None);
        }
        catch
        {
            // If the API is unreachable (e.g. cold start), treat all running runs as orphaned.
            liveAgentDtos = [];
        }
        var activeAgentJobIds = new HashSet<string>(
            liveAgentDtos.Where(a => a.ActiveJobId != null).Select(a => a.ActiveJobId!),
            StringComparer.OrdinalIgnoreCase);
        await consolidationService.CleanupOrphanedRunsAsync(activeAgentJobIds, CancellationToken.None);
    }
}
