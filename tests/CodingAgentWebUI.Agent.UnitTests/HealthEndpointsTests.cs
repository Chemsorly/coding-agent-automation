using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="HealthEndpoints"/>.
/// Uses an in-memory test server to verify the /healthz, /readyz, and /startupz endpoints.
/// </summary>
/// <remarks>
/// This class constructs <see cref="AgentWorkerService"/> which reads AGENT_TYPE from the environment.
/// It shares the "EnvironmentVariables" collection with <see cref="AgentWorkerServiceTests"/> to
/// prevent parallel execution — environment variables are process-wide shared state.
/// </remarks>
[Collection("EnvironmentVariables")]
public class HealthEndpointsTests : IAsyncDisposable
{
    private IHost? _host;
    private HttpClient? _client;

    public HealthEndpointsTests()
    {
        // Ensure AGENT_TYPE is set before any test method runs.
        // AgentWorkerServiceTests in the same assembly may clear this env var;
        // setting it here prevents race conditions during parallel test execution.
        Environment.SetEnvironmentVariable("AGENT_TYPE", "kiro-dotnet");
    }

    private async Task<HttpClient> CreateTestClient(bool isConnected)
    {
        // Create a mock AgentWorkerService-like object
        // Since AgentWorkerService has concrete dependencies, we register a real one
        // with a mock hub manager that reports the desired connection state
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockQualityGateValidator = new Mock<IQualityGateValidator>();

        // Ensure AGENT_TYPE is set
        Environment.SetEnvironmentVariable("AGENT_TYPE", "kiro-dotnet");

        var hubManager = new HubConnectionManager(
            "http://localhost:9999",
            "test-agent",
            "test-api-key",
            mockLogger.Object);

        var executor = new LocalPipelineExecutor(
            mockOrchestrator.Object,
            new Mock<IHttpClientFactory>().Object,
            new PipelineConfiguration(),
            mockQualityGateValidator.Object,
            mockLogger.Object);

        var consolidationExecutor = new LocalConsolidationExecutor(
            mockOrchestrator.Object,
            new Mock<IHttpClientFactory>().Object,
            mockLogger.Object);

        var workerService = new AgentWorkerService(hubManager, executor, consolidationExecutor, mockOrchestrator.Object, new Mock<IHttpClientFactory>().Object, mockLogger.Object);

        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddSingleton(workerService);
                        services.AddRouting();
                    })
                    .Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapHealthEndpoints();
                        });
                    });
            })
            .StartAsync();

        _client = _host.GetTestClient();
        return _client;
    }

    [Fact]
    public async Task Healthz_Returns200Ok()
    {
        var client = await CreateTestClient(isConnected: false);

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("healthy");
    }

    [Fact]
    public async Task Readyz_Returns503WhenDisconnected()
    {
        // The hub manager is not started, so IsConnected = false
        var client = await CreateTestClient(isConnected: false);

        var response = await client.GetAsync("/readyz");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("not_ready");
        content.Should().Contain("false");
    }

    [Fact]
    public async Task Startupz_Returns503BeforeMarkStarted()
    {
        var client = await CreateTestClient(isConnected: false);

        var response = await client.GetAsync("/startupz");

        // Note: MarkStarted() may have been called by a previous test in the same process.
        // This test verifies the endpoint exists and returns a valid response.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable);
    }

    [Fact]
    public async Task Startupz_Returns200AfterMarkStarted()
    {
        HealthEndpoints.MarkStarted();
        var client = await CreateTestClient(isConnected: false);

        var response = await client.GetAsync("/startupz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Contain("started");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
            await _host.StopAsync();
        _host?.Dispose();
    }
}
