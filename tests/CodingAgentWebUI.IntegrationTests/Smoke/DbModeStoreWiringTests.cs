using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.Api.Client.Stores;
using ApiBackedPipelineRunHistoryService = CodingAgentWebUI.Api.Client.Stores.ApiBackedPipelineRunHistoryService;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

/// <summary>
/// Validates that DB-mode services use correct implementations after Spec 045 / 041-045 refactoring.
///
/// Post-Spec 041-045 architecture:
///   - IConfigurationStore, IAgentProfileStore, IQualityGateConfigStore, IReviewerConfigStore
///     → ApiConfigurationStore (backed by IPipelineApiConfigClient with TTL cache)
///   - IPipelineConfigStore → ApiPipelineConfigStore
///   - IProviderConfigStore → ApiProviderConfigStore
///   - IProjectStore        → ApiProjectStore
///   - ILoopStateStore      → REMOVED (Option B: ClosedLoopAutoStart in PipelineConfiguration)
///   - IActiveRunQueryService → REMOVED (active runs via IPipelineApiRunHistoryClient)
///   - IConsolidationRunStore → ApiBackedConsolidationRunStore (migrated in Spec 041-045)
///   - IPipelineRunHistoryService → PostgresPipelineRunHistoryService (still DB-backed)
///   - IHarnessSuggestionStore   → ApiBackedHarnessSuggestionStore (migrated in Spec 041-045)
/// </summary>
[Collection("SmokeTests")]
public class DbModeStoreWiringTests : IClassFixture<DbModeWebApplicationFactory>
{
    private readonly DbModeWebApplicationFactory _factory;

    public DbModeStoreWiringTests(DbModeWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // ── Spec 045: Config stores are API-backed, not Postgres ─────────────

    [Fact]
    public void IConfigurationStore_IsApiConfigurationStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IConfigurationStore>();
        store.Should().BeOfType<ApiConfigurationStore>(
            "Spec 045 replaced PostgresConfigurationStore with ApiConfigurationStore " +
            "(backed by IPipelineApiConfigClient)");
    }

    [Fact]
    public void IPipelineConfigStore_IsApiPipelineConfigStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IPipelineConfigStore>();
        store.Should().BeOfType<ApiPipelineConfigStore>(
            "Spec 045 Req 4.2: IPipelineConfigStore is backed by IPipelineApiConfigClient");
    }

    [Fact]
    public void IProviderConfigStore_IsApiProviderConfigStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IProviderConfigStore>();
        store.Should().BeOfType<ApiProviderConfigStore>(
            "Spec 045 Req 4.2: IProviderConfigStore is backed by IPipelineApiConfigClient");
    }

    [Fact]
    public void IProjectStore_IsApiProjectStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IProjectStore>();
        store.Should().BeOfType<ApiProjectStore>(
            "Spec 045 Req 4.2: IProjectStore is backed by IPipelineApiConfigClient");
    }

    [Fact]
    public void IAgentProfileStore_IsApiConfigurationStore_PostSpec045()
    {
        var store = _factory.Services.GetRequiredService<IAgentProfileStore>();
        store.Should().BeOfType<ApiConfigurationStore>(
            "IAgentProfileStore resolves to the shared ApiConfigurationStore instance");
    }

    // ── API-backed (migrated in Spec 041-045) ────────────────────────────

    [Fact]
    public void IConsolidationRunStore_IsApiBacked_PostSpec041()
    {
        var store = _factory.Services.GetRequiredService<IConsolidationRunStore>();
        store.Should().BeOfType<ApiBackedConsolidationRunStore>(
            "IConsolidationRunStore was migrated to API-backed in Spec 041-045 to eliminate dual-write race conditions");
    }

    [Fact]
    public void IHarnessSuggestionStore_IsApiBacked_PostSpec041()
    {
        var store = _factory.Services.GetRequiredService<IHarnessSuggestionStore>();
        store.Should().BeOfType<ApiBackedHarnessSuggestionStore>(
            "IHarnessSuggestionStore was migrated to API-backed in Spec 041-045");
    }

    [Fact]
    public void IPipelineRunHistoryService_IsApiBacked_PostT8()
    {
        // T8 (arch-audit 2026-08-22): IPipelineRunHistoryService in the monolith was replaced
        // with ApiBackedPipelineRunHistoryService. Postgres-backed is now exclusively in the API host.
        var store = _factory.Services.GetRequiredService<IPipelineRunHistoryService>();
        store.Should().BeOfType<ApiBackedPipelineRunHistoryService>(
            "IPipelineRunHistoryService was migrated to API-backed in T8 (arch-audit 2026-08-22)");
    }

    [Fact]
    public void IWorkDistributor_IsKubernetes_InDbMode()
    {
        var distributor = _factory.Services.GetRequiredService<IWorkDistributor>();
        distributor.Should().BeOfType<KubernetesWorkDistributor>(
            "After Spec 041, Kubernetes is the only work distribution mode");
    }

    [Fact]
    public void IDispatchOrchestrationService_IsRegistered_InDbMode()
    {
        var service = _factory.Services.GetService<IDispatchOrchestrationService>();
        service.Should().NotBeNull(
            "IDispatchOrchestrationService must be registered — drawer services depend on it");
    }

    // ── Removed services ─────────────────────────────────────────────────
    // ILoopStateStore: removed per Spec 045 Req 1.2 (F6) Option B — loop state persisted
    //   in PipelineConfiguration.ClosedLoopAutoStart via API, not a dedicated store.
    // IActiveRunQueryService: removed per Spec 045 Req 1.2 — active runs derived from
    //   run history via IPipelineApiRunHistoryClient by filtering non-terminal steps.

    [Fact]
    public void ILoopStateStore_IsNotRegistered_PostSpec045()
    {
        var store = _factory.Services.GetService<ILoopStateStore>();
        store.Should().BeNull(
            "ILoopStateStore was removed in Spec 045 Req 1.2 (F6) Option B — " +
            "loop auto-start is now persisted in PipelineConfiguration.ClosedLoopAutoStart");
    }

    [Fact]
    public void IActiveRunQueryService_IsNotRegistered_PostSpec045()
    {
        var service = _factory.Services.GetService<IActiveRunQueryService>();
        service.Should().BeNull(
            "IActiveRunQueryService was removed in Spec 045 Req 1.2 — " +
            "active runs are now derived from IPipelineApiRunHistoryClient");
    }

    // ── Sub-Interface Consistency (API-backed adapters) ───────────────────
    // IConfigurationStore, IAgentProfileStore, IQualityGateConfigStore, IReviewerConfigStore
    // all resolve to the SAME ApiConfigurationStore instance.
    // IPipelineConfigStore, IProviderConfigStore, IProjectStore have dedicated adapters.

    [Fact]
    public void ApiConfigStore_SubInterfaces_ResolveTo_SameInstance()
    {
        var configStore = _factory.Services.GetRequiredService<IConfigurationStore>();
        var agentProfileStore = _factory.Services.GetRequiredService<IAgentProfileStore>();
        var qualityGateStore = _factory.Services.GetRequiredService<IQualityGateConfigStore>();
        var reviewerStore = _factory.Services.GetRequiredService<IReviewerConfigStore>();

        // These all resolve to the same ApiConfigurationStore singleton
        agentProfileStore.Should().BeSameAs(configStore);
        qualityGateStore.Should().BeSameAs(configStore);
        reviewerStore.Should().BeSameAs(configStore);
    }

    // ── Behavioral Validation ─────────────────────────────────────────────

    [Fact]
    public async Task HarnessSuggestionStore_DelegatesToApiClient()
    {
        // ApiBackedHarnessSuggestionStore delegates to IPipelineApiHarnessSuggestionClient.
        // The mock in DbModeWebApplicationFactory captures the call and returns the saved value.
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

        loaded.Should().NotBeNull("mock client must return the saved suggestions");
        loaded!.Suggestions.Should().HaveCount(1);
        loaded.Suggestions[0].Text.Should().Be("Use structured logging");
    }
}
