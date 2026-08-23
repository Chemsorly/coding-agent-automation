using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Regression tests verifying that <see cref="WorkItemAgentService"/> flushes the
/// <see cref="MeterProvider"/> before triggering host shutdown.
///
/// Background: ephemeral worker pods (coding-agent-worker-caa-*) run as K8s Jobs. When the
/// pod exits, the OTel SDK's PeriodicExportingMetricReader (default 60s export interval)
/// may not have had time to export the quality_gate.* counters and histograms recorded
/// during the pipeline run. The ForceFlush call in WorkItemAgentService.ExecuteAsync's
/// finally block is the mechanism that ensures metrics are pushed to the OTLP endpoint
/// before the process terminates.
///
/// These tests verify the flush happens when a <see cref="MeterProvider"/> is registered
/// in the DI service provider passed to <see cref="WorkItemAgentService"/>. Without this,
/// quality_gate_retries_total, quality_gate_evaluations_total, quality_gate_duration_seconds,
/// and quality_gate_external_ci_duration_seconds are lost when the pod terminates.
/// </summary>
public class WorkItemAgentServiceOtelFlushTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IHostApplicationLifetime> _mockLifetime = new();

    /// <summary>
    /// Verifies that WorkItemAgentService.ExecuteAsync calls MeterProvider.ForceFlush
    /// when a service provider containing a MeterProvider is supplied.
    ///
    /// This is the production path: Program.cs registers .WithMetrics(...).AddOtlpExporter(...)
    /// which results in a MeterProvider in the DI container. WorkItemAgentService receives
    /// the IServiceProvider and calls GetService&lt;MeterProvider&gt;() then ForceFlush.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithMeterProviderInServiceProvider_CallsForceFlushBeforeShutdown()
    {
        // Arrange: build a real MeterProvider that records whether ForceFlush was called.
        // We use a spy exporter to detect when ForceFlush triggers an export.
        var flushCalled = false;
        var spyExporter = new SpyExporter(onExport: () => flushCalled = true);

        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddReader(new BaseExportingMetricReader(spyExporter)
            {
                TemporalityPreference = MetricReaderTemporalityPreference.Cumulative
            })
            .Build()!;

        // Build a minimal service provider that contains the MeterProvider.
        // This mirrors the production setup in Program.cs where AddOpenTelemetry()
        // registers MeterProvider as a singleton in the DI container.
        var services = new ServiceCollection();
        services.AddSingleton(meterProvider);
        using var serviceProvider = services.BuildServiceProvider();

        // GET assignment → 410 Gone (terminal state, minimal lifecycle path)
        // TODO: [WARNING] FakeGoneHandler and HttpClient below are never disposed. Wrap both in 'using'
        // declarations (or dispose in test teardown) to satisfy IDisposable contract and avoid analyzer warnings.
        var handler = new FakeGoneHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var workItemClient = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "wi-flush-test",
            workItemClient,
            Mock.Of<IAgentConnectionManager>(),
            Mock.Of<IWorkItemExecutor>(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("test-agent"),
            _mockLifetime.Object,
            _mockLogger.Object,
            ServiceProvider: serviceProvider));

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        // TODO: [WARNING] Task.Delay here is not passed a CancellationToken. If StopApplication() fires quickly,
        // the 5s timer silently holds a timer allocation for the rest of the window after cts is disposed.
        // Pass 'cts.Token' to Task.Delay to cancel the timer as soon as the stop signal arrives.
        await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        await service.StopAsync(CancellationToken.None);

        // Assert: ForceFlush must have been invoked, which triggers an Export on the spy.
        // Without ForceFlush, the spy would never see an Export in the 10ms window between
        // the assignment fetch and host shutdown, so flushCalled would remain false.
        // TODO: [WARNING] This assertion is weakly coupled to ForceFlush: BaseExportingMetricReader.ForceFlush
        // may short-circuit Export when there are no pending measurements (empty batch), meaning flushCalled
        // could remain false even when ForceFlush IS called, or pass vacuously when called on an empty provider.
        // To make this test airtight, emit at least one measurement on a meter registered with meterProvider
        // before starting the service, so Export is guaranteed to fire only as a consequence of ForceFlush.
        flushCalled.Should().BeTrue(
            "WorkItemAgentService.ExecuteAsync must call MeterProvider.ForceFlush before triggering " +
            "host shutdown so that quality_gate.* metrics are delivered to the OTLP endpoint before " +
            "the ephemeral worker pod terminates. Without ForceFlush the PeriodicExportingMetricReader " +
            "default 60s export interval means metrics are silently lost when the pod exits.");
    }

    /// <summary>
    /// Verifies that WorkItemAgentService.ExecuteAsync does NOT throw when no MeterProvider
    /// is present in the service provider (e.g., in test environments without OTel configured).
    /// This covers the null-safe path in the source.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_WithoutMeterProviderInServiceProvider_DoesNotThrow()
    {
        // Arrange: service provider without MeterProvider (no OTel configured)
        var services = new ServiceCollection();
        using var serviceProvider = services.BuildServiceProvider();

        // TODO: [WARNING] FakeGoneHandler and HttpClient below are never disposed. Wrap both in 'using'
        // declarations (or dispose in test teardown) to satisfy IDisposable contract and avoid analyzer warnings.
        var handler = new FakeGoneHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var workItemClient = new WorkItemHttpClient(httpClient, _mockLogger.Object);

        var stopCalled = new TaskCompletionSource<bool>();
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopCalled.TrySetResult(true));

        var service = new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            "wi-no-meter",
            workItemClient,
            Mock.Of<IAgentConnectionManager>(),
            Mock.Of<IWorkItemExecutor>(),
            Mock.Of<IJobCompletionReporter>(),
            new AgentId("test-agent"),
            _mockLifetime.Object,
            _mockLogger.Object,
            ServiceProvider: serviceProvider));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var act = async () =>
        {
            await service.StartAsync(cts.Token);
            await Task.WhenAny(stopCalled.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            await service.StopAsync(CancellationToken.None);
        };

        await act.Should().NotThrowAsync(
            "WorkItemAgentService must handle absence of MeterProvider gracefully — " +
            "it should log a warning rather than throw, allowing the pod to exit cleanly " +
            "even when OTel is not configured.");
    }

    /// <summary>
    /// Verifies that the source code wires serviceProvider into the K8s-mode DI registration,
    /// ensuring the ForceFlush path is reachable at runtime.
    ///
    /// Note: the WorkItemAgentService DI factory was extracted from Program.cs into
    /// AgentWorkItemModeRegistration.cs during the S3776 complexity-reduction refactoring (#1790).
    /// This test was updated to scan the new canonical location.
    /// </summary>

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }

    private sealed class SpyExporter : BaseExporter<Metric>
    {
        private readonly Action _onExport;
        public SpyExporter(Action onExport) => _onExport = onExport;

        public override ExportResult Export(in Batch<Metric> batch)
        {
            _onExport();
            return ExportResult.Success;
        }
    }

    private sealed class FakeGoneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Gone)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
