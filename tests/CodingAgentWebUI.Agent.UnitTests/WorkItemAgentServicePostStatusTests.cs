using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="WorkItemAgentService"/> telemetry flush paths not covered by
/// <see cref="WorkItemAgentServiceOtelFlushTests"/>:
/// - MeterProvider is absent → logs Warning with "MeterProvider"
/// - MeterProvider has been disposed → ObjectDisposedException is swallowed
/// </summary>
public sealed class WorkItemAgentServicePostStatusTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IHostApplicationLifetime> _mockLifetime = new();

    private WorkItemAgentService Build(string workItemId, IServiceProvider? sp)
    {
        var handler = new GoneHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new WorkItemHttpClient(http, _mockLogger.Object);

        _mockLifetime.Setup(l => l.StopApplication()); // ensure setup exists

        return new WorkItemAgentService(new WorkItemAgentServiceDependencies(
            WorkItemId: workItemId,
            WorkItemClient: client,
            ConnectionManager: Mock.Of<IAgentConnectionManager>(),
            WorkItemExecutor: Mock.Of<IWorkItemExecutor>(),
            CompletionReporter: Mock.Of<IJobCompletionReporter>(),
            AgentId: new AgentId("test-agent"),
            Lifetime: _mockLifetime.Object,
            Logger: _mockLogger.Object,
            ServiceProvider: sp));
    }

    private async Task RunAsync(WorkItemAgentService service)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLifetime.Setup(l => l.StopApplication()).Callback(() => stopped.TrySetResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.StartAsync(cts.Token);
        await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(8)));
        await service.StopAsync(CancellationToken.None);
    }

    // ── FlushTelemetry: MeterProvider absent → logs Warning ──────────────

    [Fact]
    public async Task FlushTelemetry_NoMeterProvider_LogsWarning()
    {
        using var sp = new ServiceCollection().BuildServiceProvider();
        var service = Build("wi-no-meter", sp);

        await RunAsync(service);

        _mockLogger.Verify(l => l.Warning(
            It.Is<string>(m => m.Contains("MeterProvider"))),
            Times.AtLeastOnce,
            "Absent MeterProvider must log a Warning so operators can spot missing OTel config");
    }

    // ── FlushTelemetry: disposed ServiceProvider → ObjectDisposedException swallowed ──

    [Fact]
    public async Task FlushTelemetry_DisposedServiceProvider_DoesNotThrow()
    {
        var services = new ServiceCollection();
        services.AddOpenTelemetry().WithMetrics(m =>
            m.AddMeter(CodingAgentWebUI.Pipeline.Telemetry.PipelineTelemetry.SourceName));
        var sp = services.BuildServiceProvider();
        sp.Dispose(); // disposed before service runs

        var service = Build("wi-disposed-sp", sp);

        var act = async () => await RunAsync(service);
        await act.Should().NotThrowAsync(
            "ObjectDisposedException from disposed OTel providers must be swallowed, not propagated");
    }

    private sealed class GoneHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.Gone)
            {
                Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
