using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Scheduler;
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

    private async Task<HttpClient> CreateTestClient()
    {
        // TODO [WARNING]: This test host omits UseAuthentication()/UseAuthorization(). If authorization
        // middleware is added to Program.cs in the future, /healthz and /readyz (which currently lack
        // .AllowAnonymous()) would return 401/403 to Kubernetes probes, but these tests would still
        // pass because they never exercise the authorization pipeline. Consider adding a separate test
        // that configures services.AddAuthorization() and app.UseAuthorization() and asserts that
        // /healthz and /readyz still return 200 — this would catch the missing .AllowAnonymous()
        // calls flagged in SchedulerHealthEndpoints.cs.
        _host = await new HostBuilder()
            .ConfigureWebHost(webBuilder =>
            {
                webBuilder
                    .UseTestServer()
                    .ConfigureServices(services =>
                    {
                        services.AddRouting();
                    })
                    .Configure(appBuilder =>
                    {
                        appBuilder.UseRouting();
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
        // TODO [WARNING]: This substring assertion also passes on bodies like {"error":"not healthy"}.
        // Either drop the content assertion (HTTP 200 is the stated requirement) or tighten it to an
        // exact JSON match (e.g. content.Should().Be("{\"status\":\"healthy\"}")).
        content.Should().Contain("healthy");
    }

    [Fact]
    public async Task Readyz_Returns200Ok()
    {
        var client = await CreateTestClient();

        var response = await client.GetAsync("/readyz");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadAsStringAsync();
        // TODO [WARNING]: Same weak substring assertion as Healthz_Returns200Ok above — "ready"
        // appears in "not ready" too. Either drop the assertion or tighten to an exact JSON match.
        content.Should().Contain("ready");
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
        // TODO [WARNING]: Same weak substring assertion as the other health tests above.
        content.Should().Contain("healthy");
    }

    public async ValueTask DisposeAsync()
    {
        _client?.Dispose();
        if (_host is not null)
            await _host.StopAsync();
        _host?.Dispose();
    }
}
