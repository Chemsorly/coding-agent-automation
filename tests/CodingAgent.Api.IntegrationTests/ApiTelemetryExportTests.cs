using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Runs the telemetry export tests alone: they set <c>KUBERNETES_SERVICE_HOST</c> and build their own hosts.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TelemetryExportCollection
{
    public const string Name = "TelemetryExportCollection";
}

/// <summary>
/// End-to-end checks of what the API's real OpenTelemetry pipeline exports. A capturing exporter is added
/// behind the host's own processors, so it sees exactly what the OTLP exporter would send.
/// </summary>
[Collection(TelemetryExportCollection.Name)]
public sealed class ApiTelemetryExportTests : IDisposable
{
    private const string KubernetesHostVariable = "KUBERNETES_SERVICE_HOST";
    private const string KubernetesHost = "127.0.0.3";

    private readonly string? _previousKubernetesHost = Environment.GetEnvironmentVariable(KubernetesHostVariable);
    private readonly ApiWebApplicationFactory _baseFactory = new();
    private readonly WebApplicationFactory<Program> _factory;
    private readonly CapturingActivityExporter _spans = new();
    private readonly CapturingMetricExporter _metrics = new();

    public ApiTelemetryExportTests()
    {
        // The noise processor reads the Kubernetes API host when the host is built.
        Environment.SetEnvironmentVariable(KubernetesHostVariable, KubernetesHost);

        _factory = _baseFactory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.ConfigureOpenTelemetryTracerProvider(tracing =>
                tracing.AddProcessor(new SimpleActivityExportProcessor(_spans)));
            services.ConfigureOpenTelemetryMeterProvider(metrics =>
                metrics.AddReader(new BaseExportingMetricReader(_metrics)));
        }));
    }

    public void Dispose()
    {
        _factory.Dispose();
        _baseFactory.Dispose();
        Environment.SetEnvironmentVariable(KubernetesHostVariable, _previousKubernetesHost);
    }

    [Fact]
    public void PreInitializedCounters_AreExportedBeforeTheFirstEvent()
    {
        // Building the host runs Program.cs, including the counter pre-initialization.
        var meterProvider = _factory.Services.GetRequiredService<MeterProvider>();
        meterProvider.ForceFlush();

        _metrics.PointsFor("pipeline.run.outcomes").Should().Contain(tags =>
                tags["run_type"] == "implementation" && tags["outcome"] == "pr_created" && tags["failure_reason"] == "none",
            "pre-initialized zero series must reach the exporter, otherwise increase() misses the first event");
        _metrics.PointsFor("pipeline.run.tokens").Should().NotBeEmpty();
        _metrics.PointsFor("workdistribution.dispatch.attempts").Should().Contain(tags =>
            tags["result"] == "deferred" && tags["reason"] == "concurrency_limit");
        _metrics.PointsFor("github.api.requests").Should().NotBeEmpty();
        _metrics.PointsFor("pipeline.pull_requests.closed").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ProbeRequests_AreNotExported_WhileOtherRequestsAre()
    {
        _ = _factory.Services.GetRequiredService<TracerProvider>();
        using var client = _factory.CreateClient();

        await client.GetAsync("/healthz");
        await client.GetAsync("/readyz");
        await client.GetAsync("/api/work-items/pending");

        await WaitForAsync(() => ServerPaths().Contains("/api/work-items/pending"));
        ServerPaths().Should().NotContain(["/healthz", "/readyz"]);
    }

    [Fact]
    public void HubHeartbeatAndOutputLineSpans_AreNotExported()
    {
        _ = _factory.Services.GetRequiredService<TracerProvider>();
        using var hubSource = new ActivitySource("Microsoft.AspNetCore.SignalR.Server");

        foreach (var method in new[] { "Heartbeat", "ReportOutputLines", "RegisterAgent" })
        {
            using var activity = hubSource.StartActivity($"CodingAgent.AgentGateway.AgentHub/{method}", ActivityKind.Server);
        }

        var names = _spans.Exported.Select(a => a.DisplayName).ToList();
        names.Should().Contain("CodingAgent.AgentGateway.AgentHub/RegisterAgent");
        names.Should().NotContain([
            "CodingAgent.AgentGateway.AgentHub/Heartbeat",
            "CodingAgent.AgentGateway.AgentHub/ReportOutputLines"]);
    }

    [Fact]
    public async Task KubernetesApiCalls_AreNotExported_AndOtherClientSpansAreNamedByHost()
    {
        _ = _factory.Services.GetRequiredService<TracerProvider>();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // Nothing listens on port 1: both calls fail, but each still produces a client span.
        await IgnoreFailureAsync(() => http.GetAsync($"http://{KubernetesHost}:1/apis/coordination.k8s.io/v1/leases/lock"));
        await IgnoreFailureAsync(() => http.GetAsync("http://127.0.0.1:1/api/agents"));

        var clientSpans = _spans.Exported.Where(a => a.Kind == ActivityKind.Client).ToList();
        clientSpans.Select(a => a.DisplayName).Should().Contain("GET 127.0.0.1");
        clientSpans.Should().NotContain(a => KubernetesHost.Equals(a.GetTagItem("server.address") as string));
    }

    private List<string?> ServerPaths() =>
        _spans.Exported
            .Where(a => a.Kind == ActivityKind.Server)
            .Select(a => a.GetTagItem("url.path") as string)
            .ToList();

    private static async Task WaitForAsync(Func<bool> condition)
    {
        // Server spans end after the response has been handed to the client, so poll briefly.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static async Task IgnoreFailureAsync(Func<Task> call)
    {
        try { await call(); }
        catch (HttpRequestException) { /* expected: nothing listens on the port */ }
        catch (TaskCanceledException) { /* expected on slow connection refusal */ }
    }
}
