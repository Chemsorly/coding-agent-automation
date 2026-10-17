using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Spec 052 Req 14.10 (Task 5.5): the agent serves its health probes and nothing else (the host maps only
/// <see cref="HealthEndpoints.MapHealthEndpoints"/>, pinned by the architecture scan <c>LayerBoundaryTests.HostSurface</c>).
/// Each probe is anonymous and GET only, and its body carries only status fields, behind a fallback policy that
/// refuses everything else.
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class HealthSurfaceTests : IAsyncDisposable
{
    private static readonly string[] GetOnly = ["GET"];

    private IHost? _host;

    [Fact]
    public async Task Probes_AreTheOnlyEndpoints_AnonymousGetOnly_WithStatusBodies()
    {
        var agent = new Mock<IAgentService>();
        agent.Setup(a => a.IsConnected).Returns(true);
        _host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(s => s.AddRouting().AddSingleton(agent.Object).AddAuthorizationBuilder().SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build()))
                .Configure(app => app.UseRouting().UseAuthorization().UseEndpoints(e => e.MapHealthEndpoints())))
            .StartAsync();

        var endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        endpoints.Select(e => "/" + e.RoutePattern.RawText!.TrimStart('/')).Should().BeEquivalentTo(["/healthz", "/readyz", "/startupz"]);
        endpoints.Should().OnlyContain(e => e.Metadata.GetMetadata<IAllowAnonymous>() != null);
        endpoints.Should().OnlyContain(e => e.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.SequenceEqual(GetOnly));

        using var client = _host.GetTestClient();
        foreach (var endpoint in endpoints)
        {
            var response = await client.GetAsync("/" + endpoint.RoutePattern.RawText!.TrimStart('/'));
            response.StatusCode.Should().BeOneOf([HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable], "the probe answers itself, not the fallback");
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(["status", "connected"],
                "a probe body carries no version, connection string or ID");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }
}
