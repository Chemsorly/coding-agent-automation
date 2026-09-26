using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web;

/// <summary>
/// Extension methods for cleaning up orphaned consolidation runs at application startup.
/// </summary>
internal static class ConsolidationRehydrationExtensions
{
    /// <summary>
    /// Cleans up orphaned consolidation runs from previous sessions and rehydrates
    /// Pending run keys into the in-memory dedup tracker.
    /// </summary>
    /// <remarks>
    /// Consolidation WorkItems are created as <c>Pending</c> synchronously by
    /// <c>ConsolidationService.TriggerAsync</c> via <c>IWorkDistributor.DistributeAsync</c>.
    /// The <c>WorkItemDispatchLoop</c> picks them up and dispatches them. There is no
    /// startup retry sweep — if a trigger fails transiently, the caller must re-trigger.
    /// <para>
    /// This method performs two things:
    /// <list type="number">
    ///   <item>Marks any <c>Running</c> consolidation runs as <c>Failed</c> if no active agent
    ///         is working on them (orphaned by pod restart).</item>
    ///   <item>Adds <c>Pending</c> run keys back into the in-memory dedup tracker so that a
    ///         duplicate trigger for the same (type, templateId) is rejected until the existing
    ///         WorkItem is dispatched or fails.</item>
    /// </list>
    /// </para>
    /// <para>
    /// Must run after endpoint registration so that middleware is configured before
    /// background work begins.
    /// </para>
    /// </remarks>
    public static async Task RunConsolidationStartupAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

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
