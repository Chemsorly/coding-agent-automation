using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering observable gauges for agent metrics.
/// </summary>
/// <remarks>
/// Spec 045 M1 audit:
/// - dispatch.queue.depth removed: was backed by IPendingWorkQuery / DbPendingWorkQuery (IDbContextFactory).
///   No PrometheusRule alert references this metric — removal is safe.
///   TODO(Spec 046): restore via GET /api/work-items/pending-count on IPipelineApiWorkItemClient.
///
/// - agent.jobs.active and agent.connections.total retained but will report 0:
///   IAgentRegistryService is still registered and used in the monolith (IssueDrawerService,
///   AgentCancellationSender, AgentMonitoringPage, etc.). After Spec 044, agents register on
///   the API's registry — so the monolith's registry is permanently empty and these gauges
///   report 0. No PrometheusRule alert references these metric names, so the silent zero
///   is acceptable. Moving them to the API is a follow-up concern.
/// </remarks>
internal static class ObservableGaugeRegistrationExtensions
{
    /// <summary>
    /// Registers observable gauges for active agent jobs and total agent connections.
    /// </summary>
    public static WebApplication RegisterObservableGauges(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var agentRegistry = app.Services.GetRequiredService<IAgentRegistryService>();

        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
            () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
            () => agentRegistry.GetAllAgents().Count, "{connection}", "Total registered agents");

        return app;
    }
}
