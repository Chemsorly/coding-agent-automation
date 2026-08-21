using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using AwesomeAssertions;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Verifies that the Pipeline API's OpenTelemetry registration exports the correct meters
/// and trace sources. These tests use the real Api DI container (via ApiWebApplicationFactory)
/// to catch regressions that isolated SDK tests cannot:
///
/// - agent.jobs.active and agent.connections.total are ObservableGauges created on
///   PipelineTelemetry.Meter inside RegisterApiObservableGauges(). If PipelineTelemetry.SourceName
///   is absent from the MeterProvider's AddMeter() chain those gauges are silently dropped.
///
/// - AgentHub SignalR spans (RegisterAgent, JobAccepted, JobCompleted) are produced by
///   "Microsoft.AspNetCore.SignalR.Server". If that source is not registered with the
///   TracerProvider all hub method invocation spans are lost.
/// </summary>
[Collection("ApiIntegrationTests")]
public sealed class OtelRegistrationTests
{
    private readonly ApiWebApplicationFactory _factory;

    public OtelRegistrationTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
    }

    /// <summary>
    /// MeterProvider must be registered in DI. A null value means AddOpenTelemetry().WithMetrics()
    /// did not run or the SDK failed to build the provider.
    /// </summary>
    [Fact]
    public void Api_MeterProvider_IsRegisteredInDI()
    {
        var meterProvider = _factory.Services.GetService<MeterProvider>();
        meterProvider.Should().NotBeNull(
            "AddOpenTelemetry().WithMetrics() must register a MeterProvider; " +
            "null means the OTEL SDK is not correctly initialized in Api/Program.cs");
    }

    /// <summary>
    /// TracerProvider must be registered in DI. A null value means AddOpenTelemetry().WithTracing()
    /// did not run.
    /// </summary>
    [Fact]
    public void Api_TracerProvider_IsRegisteredInDI()
    {
        var tracerProvider = _factory.Services.GetService<TracerProvider>();
        tracerProvider.Should().NotBeNull(
            "AddOpenTelemetry().WithTracing() must register a TracerProvider; " +
            "null means tracing is not configured in Api/Program.cs");
    }

    /// <summary>
    /// Verifies that the SignalR server trace source is registered with the TracerProvider.
    ///
    /// Mechanism: when a TracerProvider subscribes to a source via .AddSource(name), the SDK
    /// attaches an ActivityListener to that ActivitySource. ActivitySource.StartActivity() returns
    /// a non-null Activity iff at least one listener is attached. If the source is not registered,
    /// StartActivity() returns null even though the TracerProvider is active.
    ///
    /// This test creates an ActivitySource with the expected name in the same process where the
    /// WebApplicationFactory TracerProvider is active — the provider's listener is already
    /// installed on any source whose name matches what was passed to AddSource().
    /// </summary>
    [Fact]
    public void Api_TracerProvider_ListensToSignalRServerSource()
    {
        // Ensure TracerProvider is built and active (confirmed by the DI test above)
        _ = _factory.Services.GetRequiredService<TracerProvider>();

        using var source = new System.Diagnostics.ActivitySource("Microsoft.AspNetCore.SignalR.Server");
        using var activity = source.StartActivity("TestHubInvocation");

        activity.Should().NotBeNull(
            "TracerProvider must subscribe to \"Microsoft.AspNetCore.SignalR.Server\" so that " +
            "AgentHub method invocations (RegisterAgent, JobAccepted, JobCompleted) produce traces. " +
            "A null Activity means the source is not registered — add " +
            ".AddSource(\"Microsoft.AspNetCore.SignalR.Server\") to the Api's WithTracing() config.");
    }

    /// <summary>
    /// Verifies the PipelineTelemetry.SourceName meter name constant is consistent with what the
    /// Api registers. If someone registers a literal string in Program.cs instead of the constant,
    /// a rename of the constant breaks the export silently — this test catches that drift.
    /// </summary>
    [Fact]
    public void PipelineTelemetry_SourceName_MatchesExpectedMeterName()
    {
        // The meter name registered in Api/Program.cs must match PipelineTelemetry.SourceName.
        // agent.jobs.active and agent.connections.total are created on PipelineTelemetry.Meter
        // via RegisterApiObservableGauges(); if the meter name drifts those gauges are lost.
        PipelineTelemetry.SourceName.Should().Be("CodingAgent.Pipeline",
            "Api/Program.cs registers .AddMeter(PipelineTelemetry.SourceName); changing the " +
            "constant value without updating the Helm/OTEL collector filter would break metric export");
    }

    /// <summary>
    /// Verifies the WorkDistributionTelemetry.MeterName constant is consistent.
    /// </summary>
    [Fact]
    public void WorkDistributionTelemetry_MeterName_MatchesExpectedName()
    {
        WorkDistributionTelemetry.MeterName.Should().Be("CodingAgent.WorkDistribution",
            "Api/Program.cs, WebUI/Program.cs, and JobController/Program.cs all register " +
            ".AddMeter(WorkDistributionTelemetry.MeterName); changing the constant without " +
            "updating those call sites would silently stop all workdistribution.* metric export");
    }
}
