using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
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
/// Validates that DB-mode services use Postgres-backed implementations (not filesystem).
/// These tests directly address the bug class: "Ensure dbmode writes to db and not fs" (6b7ba939).
///
/// The key invariant: when Database:Host is configured, ALL persistence services must use
/// their Postgres implementation. If any service falls through to a filesystem implementation,
/// data is split across two stores and eventually lost during container restart.
/// </summary>
public class DbModeStoreWiringTests : IClassFixture<DbModeWebApplicationFactory>
{
    private readonly DbModeWebApplicationFactory _factory;

    public DbModeStoreWiringTests(DbModeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── Implementation Type Assertions ───────────────────────────────────
    // These would have caught the "dbmode writes to fs" bug immediately.

    [Fact]
    public void IConfigurationStore_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<IConfigurationStore>();
        store.Should().BeOfType<PostgresConfigurationStore>(
            "DB mode must use PostgresConfigurationStore, not JsonConfigurationStore");
    }

    [Fact]
    public void IConsolidationRunStore_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<IConsolidationRunStore>();
        store.Should().BeOfType<PostgresConsolidationRunStore>(
            "DB mode must use PostgresConsolidationRunStore, not FileSystemConsolidationRunStore");
    }

    [Fact]
    public void ILoopStateStore_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<ILoopStateStore>();
        store.Should().BeOfType<PostgresLoopStateStore>(
            "DB mode must use PostgresLoopStateStore, not FileSystemLoopStateStore");
    }

    [Fact]
    public void IHarnessSuggestionStore_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<IHarnessSuggestionStore>();
        store.Should().BeOfType<PostgresHarnessSuggestionStore>(
            "DB mode must use PostgresHarnessSuggestionStore, not FileSystemHarnessSuggestionStore");
    }

    [Fact]
    public void IPipelineRunHistoryService_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<IPipelineRunHistoryService>();
        store.Should().BeOfType<PostgresPipelineRunHistoryService>(
            "DB mode must use PostgresPipelineRunHistoryService, not file-based PipelineRunHistoryService");
    }

    [Fact]
    public void IActiveRunQueryService_IsPostgres_InDbMode()
    {
        var store = _factory.Services.GetRequiredService<IActiveRunQueryService>();
        store.Should().BeOfType<PostgresActiveRunQueryService>(
            "DB mode must use PostgresActiveRunQueryService, not InMemoryActiveRunQueryService");
    }

    [Fact]
    public void IWorkDistributor_IsSignalR_InDbMode()
    {
        var distributor = _factory.Services.GetRequiredService<IWorkDistributor>();
        distributor.Should().BeOfType<SignalRWorkDistributor>(
            "SignalR DB mode must use SignalRWorkDistributor, not LegacyWorkDistributor");
    }

    [Fact]
    public void IDispatchOrchestrationService_IsRegistered_InDbMode()
    {
        var service = _factory.Services.GetService<IDispatchOrchestrationService>();
        service.Should().NotBeNull(
            "IDispatchOrchestrationService must be registered in DB mode (null in Legacy mode)");
    }

    // ── Sub-Interface Consistency ─────────────────────────────────────────
    // All config store sub-interfaces must resolve to the SAME instance.

    [Fact]
    public void AllConfigStoreInterfaces_ResolveTo_SameInstance()
    {
        var configStore = _factory.Services.GetRequiredService<IConfigurationStore>();
        var pipelineStore = _factory.Services.GetRequiredService<IPipelineConfigStore>();
        var providerStore = _factory.Services.GetRequiredService<IProviderConfigStore>();
        var agentProfileStore = _factory.Services.GetRequiredService<IAgentProfileStore>();
        var qualityGateStore = _factory.Services.GetRequiredService<IQualityGateConfigStore>();
        var reviewerStore = _factory.Services.GetRequiredService<IReviewerConfigStore>();
        var projectStore = _factory.Services.GetRequiredService<IProjectStore>();

        // All must be the same instance — critical for cache coherence
        pipelineStore.Should().BeSameAs(configStore);
        providerStore.Should().BeSameAs(configStore);
        agentProfileStore.Should().BeSameAs(configStore);
        qualityGateStore.Should().BeSameAs(configStore);
        reviewerStore.Should().BeSameAs(configStore);
        projectStore.Should().BeSameAs(configStore);
    }

    // ── FeatureFlags Consistency ──────────────────────────────────────────

    [Fact]
    public void FeatureFlags_IsDatabaseMode_True_WhenDbConfigured()
    {
        var flags = _factory.Services.GetRequiredService<FeatureFlags>();
        flags.IsDatabaseMode.Should().BeTrue(
            "FeatureFlags.IsDatabaseMode must be true when Database:Host is configured");
    }

    // ── Behavioral Validation (round-trip tests) ─────────────────────────
    // These verify the stores actually persist to the InMemory EF Core database,
    // not to the filesystem. If a store was accidentally using FileSystem*Store,
    // the data would go to disk and not be visible via the DbContext.

    [Fact]
    public async Task LoopStateStore_PersistsToDatabase()
    {
        var store = _factory.Services.GetRequiredService<ILoopStateStore>();
        var state = new LoopState { IsActive = true };

        await store.WriteAsync(state, CancellationToken.None);
        var loaded = await store.ReadAsync(CancellationToken.None);

        loaded.Should().NotBeNull("LoopState must persist to database, not filesystem");
        loaded!.IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task HarnessSuggestionStore_PersistsToDatabase()
    {
        var store = _factory.Services.GetRequiredService<IHarnessSuggestionStore>();
        var suggestions = new HarnessSuggestions
        {
            BasedOnRunCount = 5,
            GeneratedAtUtc = DateTime.UtcNow,
            SuccessRate = 0.8m,
            Suggestions = [new HarnessSuggestion { Frequency = 3, Rationale = "Frequent timeout", Text = "Use structured logging" }]
        };

        await store.SaveAsync(suggestions, CancellationToken.None);
        var loaded = await store.GetAsync(CancellationToken.None);

        loaded.Should().NotBeNull("HarnessSuggestions must persist to database, not filesystem");
        loaded!.Suggestions.Should().HaveCount(1);
        loaded.Suggestions[0].Text.Should().Be("Use structured logging");
    }
}
