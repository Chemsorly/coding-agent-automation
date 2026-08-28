using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Scheduler;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Integration-style tests for the scheduler's health probe endpoints.
/// Verifies that /healthz, /readyz, and /health all return HTTP 200
/// so that Kubernetes startup/liveness/readiness probes and the Dockerfile
/// HEALTHCHECK can all succeed.
/// </summary>
/// <remarks>
/// Uses a minimal in-memory host (HostBuilder + UseTestServer) rather than
/// WebApplicationFactory&lt;Program&gt; to avoid triggering the fast-fail guards
/// in Program.cs (PipelineApi__BaseUrl and AGENT_API_KEY checks).
/// The endpoint registrations are imported from <see cref="SchedulerHealthEndpoints.MapSchedulerHealthEndpoints"/>
/// — the exact same method called by Program.cs — so these tests exercise the production
/// endpoint configuration rather than inline copies of the handlers.
/// Pattern matches CodingAgentWebUI.Agent.UnitTests.HealthEndpointsTests.
/// </remarks>
public class SchedulerHealthEndpointsTests : IAsyncDisposable
{
    private IHost? _host;
    private HttpClient? _client;

    private async Task<HttpClient> CreateTestClient(bool withAuthorization = false)
    {
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                        if (withAuthorization)
                        {
                            services.AddAuthorization(options =>
                            {
                                // Default policy requires authentication — everything is locked down
                                // unless endpoints explicitly opt out via .AllowAnonymous().
                                options.FallbackPolicy = new AuthorizationPolicyBuilder()
                                    .RequireAuthenticatedUser()
                                    .Build();
                            });
                        }
                    })
                    .Configure(appBuilder =>
                    {
                        appBuilder.UseRouting();
                        if (withAuthorization)
                            appBuilder.UseAuthorization();
                        appBuilder.UseEndpoints(endpoints =>
                        {
                            // Calls the same extension method used by Program.cs.
                            // If /healthz or /readyz are removed from SchedulerHealthEndpoints,
                            // these tests will fail — providing genuine regression protection.
                            endpoints.MapSchedulerHealthEndpoints();
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
        var client = await CreateTestClient();

        var response = await client.GetAsync("/healthz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("{\"status\":\"healthy\"}");
    }

    [Fact]
    public async Task Readyz_Returns200Ok()
    {
        var client = await CreateTestClient();

        var response = await client.GetAsync("/readyz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("{\"status\":\"ready\"}");
    }

    [Fact]
    public async Task Health_Returns200Ok_BackwardCompatibility()
    {
        // Verifies the original /health endpoint is retained so the
        // Dockerfile HEALTHCHECK (curl -f http://localhost:8080/health) still passes.
        var client = await CreateTestClient();

        var response = await client.GetAsync("/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        content.Should().Be("{\"status\":\"healthy\"}");
    }

    /// <summary>
    /// Verifies that all three health probes remain reachable when authorization middleware
    /// is active with a deny-by-default fallback policy. This guards against accidentally
    /// removing .AllowAnonymous() from any of the probe endpoints.
    /// </summary>
    [Theory]
    [InlineData("/healthz")]
    [InlineData("/readyz")]
    [InlineData("/health")]
    public async Task HealthProbes_Return200Ok_WhenAuthorizationMiddlewareIsActive(string path)
    {
        var client = await CreateTestClient(withAuthorization: true);

        var response = await client.GetAsync(path);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            because: $"{path} must be reachable without authentication so Kubernetes probes succeed");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
            await _host.StopAsync();
        _host?.Dispose();
    }
}
