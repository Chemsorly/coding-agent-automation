using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for rehydrating active pipeline runs from the database at startup.
/// Restores in-memory run state from persisted WorkItems so that GetActiveRuns() returns
/// accurate data immediately on restart — no observability gap.
/// </summary>
internal static class ActiveRunRehydrationExtensions
{
    /// <summary>
    /// Rehydrates active pipeline runs from the WorkItems table (DB mode only).
    /// Must run AFTER <see cref="DatabaseStartupExtensions.InitializeDatabaseAsync"/> (needs DB)
    /// and BEFORE <c>app.Run()</c> (before HeartbeatMonitor starts).
    /// </summary>
    /// <remarks>
    /// In legacy mode (no DB connection string), returns immediately.
    /// </remarks>
    public static async Task RehydrateActiveRunsAsync(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // TODO: Connection string is re-resolved from app.Configuration here rather than using the
        // pre-computed dbConnectionString local from Program.cs. These should always match, but if
        // configuration sources are added between Build() and this call, behavior could diverge.
        // Consider accepting dbConnectionString as a parameter for consistency. (review-findings)
        var connectionString = DatabaseConnectionResolver.Resolve(app.Configuration);
        if (string.IsNullOrEmpty(connectionString))
            return; // Legacy mode — no WorkItems table to rehydrate from

        var rehydrationDbFactory = app.Services.GetRequiredService<IDbContextFactory<PipelineDbContext>>();
        await using var rehydrationDb = await rehydrationDbFactory.CreateDbContextAsync();

        var activeWorkItems = await rehydrationDb.WorkItems
            .AsNoTracking()
            .Where(w => (w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
                     && w.TaskType != WorkItemTaskType.Consolidation)
            .ToListAsync();

        if (activeWorkItems.Count == 0)
            return;

        var runService = app.Services.GetRequiredService<OrchestratorRunService>();
        var rehydratedCount = 0;

        foreach (var item in activeWorkItems)
        {
            if (string.IsNullOrEmpty(item.Payload)) continue;

            try
            {
                var request = System.Text.Json.JsonSerializer.Deserialize<JobDistributionRequest>(
                    item.Payload, PipelineJsonOptions.Default);
                if (request is null || string.IsNullOrEmpty(request.RunId)) continue;

                // AgentId intentionally null: HeartbeatMonitor Phase 3 skips runs without AgentId,
                // preventing false-positive orphan detection before agents reconnect.
                // Agents set AgentId on reconnect via AgentHub.RegisterAgent.
                // TODO: CurrentStep is approximated — Running items may have been in reviewing/building/retrying
                // phase. The agent will update CurrentStep on reconnect, but until then the UI may show an
                // inaccurate step for rehydrated runs.
                var initialStep = item.Status == WorkItemStatus.Running
                    ? PipelineStep.GeneratingCode
                    : PipelineStep.Created;

                var run = PipelineRunFactory.FromDistributionRequest(
                    request, agentId: null, initialStep,
                    startedAt: item.DispatchedAt ?? item.CreatedAt);
                runService.AddRun(run);
                rehydratedCount++;
            }
            catch (System.Text.Json.JsonException ex)
            {
                Log.Warning(ex, "Failed to deserialize WorkItem {WorkItemId} payload during rehydration — skipping", item.Id);
            }
        }

        if (rehydratedCount > 0)
            Log.Information("Rehydrated {Count} active pipeline runs from WorkItems table", rehydratedCount);
    }
}
