using CodingAgent.Web.IntegrationTests.Helpers;
using System.Net;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Stores;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Api.Client.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Smoke;

/// <summary>
/// Integration smoke tests for DB mode (SignalR work distribution with InMemory EF Core).
/// Validates that the entire DI container resolves correctly when the app boots in database mode.
/// These tests would have caught the bugs from the Postgres introduction:
/// - "Ensure dbmode writes to db and not fs" (ConsolidationRunStore wiring)
/// - "hide Data Management section in JSON-file mode to prevent circuit crash" (FeatureFlags)
/// - Missing IConsolidationRunStore, ILoopStateStore registrations
/// </summary>
[Collection("SmokeTests")]
public class DbModeSmokeTests : IClassFixture<DbModeWebApplicationFactory>
{
    private readonly DbModeWebApplicationFactory _factory;

    public DbModeSmokeTests(DbModeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── DI Resolution Tests ──────────────────────────────────────────────

    [Fact]
    public void App_Starts_In_DbMode_Without_Throwing()
    {
        // Explicit assertion: factory must start without throwing (DI wiring is valid)
        var exception = Record.Exception(() => _factory.CreateClient());
        Assert.Null(exception);
    }

    [Fact]
    public async Task AppServesRequests_InDbMode()
    {
        using var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory);
        // Root page (Blazor) — verifies the app responds to HTTP requests
        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── Blazor Pages Load Without Server Error ─────────────────────────

    [Fact]
    public async Task SettingsPage_DoesNotCrash_InDbMode()
    {
        using var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory);
        var response = await client.GetAsync("/settings");

        // Blazor routes may return 200 (SSR) or 404 (client-side routing),
        // but never 500 (circuit crash / DI failure / unhandled exception)
        ((int)response.StatusCode).Should().BeLessThan(500,
            "Settings page should not cause a server error in DB mode");
    }

    [Fact]
    public async Task AgentCodingPage_Returns_OK_InDbMode()
    {
        using var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory);
        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MonitoringPage_DoesNotCrash_InDbMode()
    {
        using var client = await AuthTestEnvironment.CreateSignedInClientAsync(_factory);
        var response = await client.GetAsync("/monitoring");

        ((int)response.StatusCode).Should().BeLessThan(500,
            "Monitoring page should not cause a server error in DB mode");
    }
}
