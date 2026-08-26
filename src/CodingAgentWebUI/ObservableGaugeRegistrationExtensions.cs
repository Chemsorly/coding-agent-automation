namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering observable gauges for agent metrics.
/// </summary>
/// <remarks>
/// Gauge migration audit:
/// - dispatch.queue.depth: removed — was backed by IPendingWorkQuery / DbPendingWorkQuery (IDbContextFactory).
///   No PrometheusRule alert references this metric. Restore via GET /api/work-items/pending-count
///   on IPipelineApiWorkItemClient when queue depth monitoring is needed.
///
/// - agent.jobs.active and agent.connections.total: moved to CodingAgentWebUI.Api
///   (<see cref="ApiStartupExtensions.RegisterApiObservableGauges"/>).
///   Agents register on the API hub, so the API is the only process holding first-hand agent state.
///   The monolith now sees agents through <c>ApiAgentRegistryService</c>, a polled snapshot of
///   GET /api/agents — fine for rendering presence, but a second-hand and slightly delayed source.
///   The gauges stay on the API side so the metrics are emitted by the owner of the data.
internal static class ObservableGaugeRegistrationExtensions
{
    /// <summary>
    /// No-op: all gauges have moved to the API process.
    /// Call retained so existing <c>app.RegisterObservableGauges()</c> in Program.cs compiles.
    /// </summary>
    public static WebApplication RegisterObservableGauges(this WebApplication app)
    {
        return app;
    }
}
