using System.Net;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Stores;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Integration smoke tests for DB mode (SignalR work distribution with InMemory EF Core).
/// Validates that the entire DI container resolves correctly when the app boots in database mode.
/// These tests would have caught the bugs from the Postgres introduction:
/// - "Ensure dbmode writes to db and not fs" (ConsolidationRunStore wiring)
/// - "hide Data Management section in JSON-file mode to prevent circuit crash" (FeatureFlags)
/// - Missing IConsolidationRunStore, ILoopStateStore, IHarnessSuggestionStore registrations
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
        using var client = _factory.CreateClient();
        // Root page (Blazor) — verifies the app responds to HTTP requests
        var response = await client.GetAsync("/");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData(typeof(IWorkDistributor))]
    [InlineData(typeof(IConsolidationRunStore))]
    [InlineData(typeof(IHarnessSuggestionStore))]
    [InlineData(typeof(IPipelineRunHistoryService))]
    [InlineData(typeof(IDbContextFactory<PipelineDbContext>))]
    // Note: IConfigurationStore, IAgentProfileStore, IQualityGateConfigStore, IReviewerConfigStore
    // resolve to ApiConfigurationStore (Spec 045); IPipelineConfigStore → ApiPipelineConfigStore;
    // IProviderConfigStore → ApiProviderConfigStore; IProjectStore → ApiProjectStore.
    // IActiveRunQueryService and ILoopStateStore removed in Spec 045 Req 1.2.
    public void DbMode_KeyService_Resolves(Type serviceType)
    {
        var service = _factory.Services.GetService(serviceType);
        service.Should().NotBeNull($"{serviceType.Name} should be registered in DB mode");
    }

    // ── Configuration Store Is API-Backed (Spec 045) ─────────────────────

    [Fact]
    public void ConfigurationStore_IsApiConfigurationStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IConfigurationStore>();
        store.Should().BeOfType<ApiConfigurationStore>(
            "Spec 045 replaced PostgresConfigurationStore with ApiConfigurationStore");
    }

    [Fact]
    public async Task ConfigurationStore_LoadPipelineConfig_ReturnsNonNull()
    {
        // ApiConfigurationStore delegates to the mocked IPipelineApiConfigClient,
        // which returns new PipelineConfiguration() for GetPipelineConfigAsync.
        var store = _factory.Services.GetRequiredService<IConfigurationStore>();
        var config = await store.LoadPipelineConfigAsync(CancellationToken.None);

        config.Should().NotBeNull();
    }

    // ConfigurationStore_SaveAndLoad_RoundTrips is removed: the mock IPipelineApiConfigClient
    // returns a fixed PipelineConfiguration() and does not persist mutations, so round-trip
    // testing requires a real API. The round-trip behaviour is covered by API integration tests.

    // ── Work Distributor Is DB-Backed ────────────────────────────────────

    [Fact]
    public void WorkDistributor_IsKubernetesImplementation()
    {
        var distributor = _factory.Services.GetRequiredService<IWorkDistributor>();
        distributor.Should().BeOfType<KubernetesWorkDistributor>();
    }

    [Fact]
    public async Task WorkDistributor_GetActiveIssueIdentifiers_ReturnsEmpty()
    {
        var distributor = _factory.Services.GetRequiredService<IWorkDistributor>();
        var active = await distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkDistributor_IsIssueDistributed_ReturnsFalse_WhenEmpty()
    {
        var distributor = _factory.Services.GetRequiredService<IWorkDistributor>();
        var result = await distributor.IsIssueDistributedAsync("org/repo#1", "provider-1", CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── Consolidation Store Is API-Backed (Spec 041-045) ─────────────────

    [Fact]
    public void ConsolidationRunStore_IsApiBackedImplementation()
    {
        var store = _factory.Services.GetRequiredService<IConsolidationRunStore>();
        store.GetType().Name.Should().Contain("ApiBacked");
    }

    // ── Blazor Pages Load Without Server Error ─────────────────────────

    [Fact]
    public async Task SettingsPage_DoesNotCrash_InDbMode()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/settings");

        // Blazor routes may return 200 (SSR) or 404 (client-side routing),
        // but never 500 (circuit crash / DI failure / unhandled exception)
        ((int)response.StatusCode).Should().BeLessThan(500,
            "Settings page should not cause a server error in DB mode");
    }

    [Fact]
    public async Task AgentCodingPage_Returns_OK_InDbMode()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task MonitoringPage_DoesNotCrash_InDbMode()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync("/monitoring");

        ((int)response.StatusCode).Should().BeLessThan(500,
            "Monitoring page should not cause a server error in DB mode");
    }

    // ── Pipeline History Service Is Registered ─────────────────────────

    [Fact]
    public void PipelineRunHistoryService_IsRegistered_InDbMode()
    {
        var svc = _factory.Services.GetRequiredService<IPipelineRunHistoryService>();
        svc.Should().NotBeNull();
    }
}
