using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering observable gauges for dispatch and agent metrics.
/// </summary>
internal static class ObservableGaugeRegistrationExtensions
{
    /// <summary>
    /// Registers observable gauges for dispatch queue depth, active agent jobs,
    /// and total agent connections. These gauges are scraped by the OpenTelemetry metrics pipeline.
    /// </summary>
    /// <remarks>
    /// No ordering dependencies on other startup methods — services are resolved from DI.
    /// </remarks>
    public static WebApplication RegisterObservableGauges(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var agentRegistry = app.Services.GetRequiredService<IAgentRegistryService>();
        var pendingWorkQuery = app.Services.GetRequiredService<IPendingWorkQuery>();

        _ = PipelineTelemetry.Meter.CreateObservableGauge("dispatch.queue.depth",
            () => pendingWorkQuery.PendingCount, "{item}", "Jobs waiting for available agent");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.jobs.active",
            () => agentRegistry.GetBusyAgentCount(), "{job}", "Currently executing agent jobs");
        _ = PipelineTelemetry.Meter.CreateObservableGauge("agent.connections.total",
            () => agentRegistry.GetAllAgents().Count, "{connection}", "Total registered agents");

        return app;
    }
}
