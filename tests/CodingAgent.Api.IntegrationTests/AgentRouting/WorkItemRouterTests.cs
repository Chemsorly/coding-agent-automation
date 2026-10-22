using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Moq;
using Xunit;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Spec 053 task 4.2: <see cref="WorkItemRouter"/> resolves a work item's route once, when the API creates it (req 4.1,
/// 4.3), and <see cref="WorkItemStore.CreateAsync"/> routes after the idempotent-retry check. Also that the stored
/// payload keeps the pinned route (<see cref="WorkItemDispatchEndpoints.BuildMinimalPayload"/>).
/// </summary>
public sealed class WorkItemRouterTests
{
    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());

    private static readonly AgentProfile Kiro = AgentRoutingTestData.Profile("kiro", "dotnet,kiro", "Kiro");
    private static readonly AgentProfile Claude = AgentRoutingTestData.Profile("claude", "claude,dotnet", "Claude");
    private static readonly AgentProfile Brain = AgentRoutingTestData.Profile("brain", "dotnet,kiro", "Brain");

    private static readonly CodingAgent.Kubernetes.JobTemplateStore JobTemplates = AgentRoutingTestData.JobTemplates(
        ("dotnet,kiro", JobTemplateProviderType.Kiro), ("claude,dotnet", JobTemplateProviderType.Claude));

    /// <summary>The repository's template: its Implementation row routes to Claude.</summary>
    private static readonly PipelineJobTemplate RepoTemplate = new()
    {
        Id = "t-repo", Name = "checkout-api", IssueProviderId = "ip-1", RepoProviderId = "repo-1", Enabled = true,
        AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
        {
            [WorkItemTaskType.Implementation] = AgentRoutingTestData.Route("claude", "repo-model"),
            [WorkItemTaskType.Consolidation] = AgentRoutingTestData.Route("claude", "repo-consolidation-model"),
        },
    };

    /// <summary>The template that triggers a consolidation: its Consolidation row routes to Brain.</summary>
    private static readonly PipelineJobTemplate ConsolidationTemplate = new()
    {
        Id = "t-brain", Name = "brain", IssueProviderId = "ip-2", RepoProviderId = "repo-2", Enabled = true,
        AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
        {
            [WorkItemTaskType.Consolidation] = AgentRoutingTestData.Route("brain", "brain-model"),
        },
    };

    private WorkItemRouter Router(
        IReadOnlyList<PipelineJobTemplate>? templates = null, CodingAgent.Kubernetes.JobTemplateStore? jobTemplates = null) =>
        AgentRoutingTestSupport.Router(_db, [Kiro, Claude, Brain], templates ?? [RepoTemplate, ConsolidationTemplate], jobTemplates ?? JobTemplates);

    private void SeedRouting(RoutingTable? routing = null) =>
        AgentRoutingTestSupport.SeedRouting(_db, routing ?? AgentRoutingTestData.Routing("kiro", "default-model", "high",
            (WorkItemTaskType.Review, AgentRoutingTestData.Route("claude", "review-model"))));

    private static JobDistributionRequest Request(WorkItemTaskType taskType, string repo = "repo-1", string? consolidationTemplateId = null) => new()
    {
        IssueIdentifier = new IssueIdentifier($"{taskType}:{Guid.NewGuid():N}"),
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = repo,
        InitiatedBy = "test",
        TaskType = taskType,
        ConsolidationTemplateId = consolidationTemplateId,
        TimeoutSeconds = 3600,
    };

    private async Task<JobDistributionRequest> RoutedAsync(JobDistributionRequest request, WorkItemRouter? router = null)
    {
        var result = await (router ?? Router()).RouteAsync(request, PipelineTelemetry.RoutingStages.Create, CancellationToken.None);
        result.IsRefused.Should().BeFalse(result.RefusalText);
        return result.Request!;
    }

    // ── Req 4.3: which template's rows apply, per entry point ────────────────

    [Fact]
    public async Task TheRepositorysTemplateRow_WinsOverTheGlobalRows()
    {
        SeedRouting();

        var routed = await RoutedAsync(Request(WorkItemTaskType.Implementation));

        routed.Route.Should().Be(new AgentRouteSnapshot(
            "claude", "Claude", JobTemplateProviderType.Claude, "repo-model", "auto", AgentRouteSource.Repository));
        routed.AgentSelector.Should().Be("claude,dotnet", "the selector is the routed profile's job template key");
        routed.ResolvedProfileId.Should().Be("claude");
    }

    [Fact]
    public async Task WithoutARepositoryRow_TheGlobalTaskRowApplies()
    {
        SeedRouting();

        var routed = await RoutedAsync(Request(WorkItemTaskType.Review));

        routed.Route!.Should().BeEquivalentTo(new { ProfileId = "claude", Model = "review-model", Source = AgentRouteSource.GlobalTaskRow });
    }

    [Theory]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    public async Task WithoutATaskRow_TheGlobalDefaultApplies(WorkItemTaskType taskType)
    {
        SeedRouting();

        var routed = await RoutedAsync(Request(taskType));

        routed.Route!.Should().BeEquivalentTo(new
        {
            ProfileId = "kiro", Model = "default-model", Effort = "high", Source = AgentRouteSource.GlobalDefault,
        });
    }

    [Fact]
    public async Task ARepositoryNoTemplateOwns_GetsTheGlobalRowsOnly()
    {
        // RepositorySettings.TemplateFor returns null: no repository rows apply.
        SeedRouting();

        var routed = await RoutedAsync(Request(WorkItemTaskType.Implementation, repo: "repo-unknown"));

        routed.Route!.Source.Should().Be(AgentRouteSource.GlobalDefault);
        routed.ResolvedProfileId.Should().Be("kiro");
    }

    [Fact]
    public async Task AConsolidation_UsesTheRowsOfTheTemplateThatTriggeredIt_NotTheRepositorys()
    {
        SeedRouting();

        var routed = await RoutedAsync(Request(WorkItemTaskType.Consolidation, repo: "repo-1", consolidationTemplateId: "t-brain"));

        routed.Route!.Should().BeEquivalentTo(new { ProfileId = "brain", Model = "brain-model", Source = AgentRouteSource.Repository });
    }

    [Fact]
    public async Task AGlobalConsolidation_UsesTheGlobalRows()
    {
        SeedRouting();

        var routed = await RoutedAsync(Request(WorkItemTaskType.Consolidation, repo: "repo-1"));

        routed.Route!.Source.Should().Be(AgentRouteSource.GlobalDefault, "a run without a template has no repository rows");
    }

    [Fact]
    public async Task TheSourceCanBeOverridden_ForTheDispatchersRolloutRouting()
    {
        SeedRouting();

        var result = await Router().RouteAsync(
            Request(WorkItemTaskType.Decomposition), PipelineTelemetry.RoutingStages.Dispatch, CancellationToken.None, AgentRouteSource.Dispatch);

        result.Request!.Route!.Source.Should().Be(AgentRouteSource.Dispatch);
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    [InlineData(WorkItemTaskType.Consolidation)]
    public async Task WithoutARoute_EveryTaskTypeIsRefused_D15(WorkItemTaskType taskType)
    {
        // No routing table stored at all: nothing picks a profile no route names.
        var result = await Router(templates: []).RouteAsync(Request(taskType, repo: "repo-unknown"), PipelineTelemetry.RoutingStages.Create, CancellationToken.None);

        result.IsRefused.Should().BeTrue();
        result.RefusalReason.Should().Be(AgentRouteRefusalReason.NoRoute);
        result.RefusalText.Should().Be(AgentRouteRefusalReason.NoRouteText);
    }

    [Fact]
    public async Task AConsolidationWhoseProfileHasNoJobTemplate_IsRefusedAtCreation()
    {
        // #3642: the dispatcher could never start it.
        SeedRouting();

        var result = await Router(jobTemplates: AgentRoutingTestData.JobTemplates(("claude,dotnet", JobTemplateProviderType.Claude)))
            .RouteAsync(Request(WorkItemTaskType.Consolidation, consolidationTemplateId: "t-brain"), PipelineTelemetry.RoutingStages.Create, CancellationToken.None);

        result.RefusalReason.Should().Be(AgentRouteRefusalReason.TemplateMissing);
        result.RefusalText.Should().Be("Job template dotnet,kiro of agent profile 'Brain' does not exist in the Helm values");
    }

    [Fact]
    public async Task OtherTaskTypesWhoseJobTemplateIsMissing_AreRoutedAnyway()
    {
        // The dispatcher defers them and alerts; only a consolidation is checked here.
        SeedRouting();

        var routed = await RoutedAsync(
            Request(WorkItemTaskType.Decomposition),
            Router(jobTemplates: AgentRoutingTestData.JobTemplates(("claude,dotnet", JobTemplateProviderType.Claude))));

        routed.AgentSelector.Should().Be("dotnet,kiro");
    }

    [Fact]
    public async Task ARouteToADisabledProfile_IsRefused_ProfileUnavailable()
    {
        SeedRouting(AgentRoutingTestData.Routing("kiro"));
        var router = AgentRoutingTestSupport.Router(_db, [Kiro with { Enabled = false }], [], JobTemplates);

        var result = await router.RouteAsync(Request(WorkItemTaskType.Implementation), PipelineTelemetry.RoutingStages.Create, CancellationToken.None);

        result.RefusalReason.Should().Be(AgentRouteRefusalReason.ProfileUnavailable);
    }

    // ── WorkItemStore: the idempotent retry is answered before routing ───────

    [Fact]
    public async Task CreateAsync_AnIdempotentRetry_FindsItsRow_EvenAfterTheRouteWasRemoved()
    {
        SeedRouting();
        var store = new WorkItemStore(_db, new OrchestratorRunService(Mock.Of<Serilog.ILogger>()), Router());
        var request = Request(WorkItemTaskType.Implementation) with { RunId = Guid.NewGuid().ToString() };

        var first = await store.CreateAsync(request, CancellationToken.None);
        await using (var db = _db.CreateDbContext())
        {
            db.KeyValueStore.RemoveRange(db.KeyValueStore);
            await db.SaveChangesAsync();
        }
        var retry = await store.CreateAsync(request, CancellationToken.None);

        first.Outcome.Should().Be(WorkItemCreateOutcome.Created);
        retry.Should().Be((WorkItemCreateOutcome.AlreadyCreated, first.WorkItemId, (string?)null),
            "a retried create answers from its row, not from the routing table (review-2 n5)");
    }

    [Fact]
    public async Task CreateAsync_ARefusal_WritesNothing_AndReturnsTheReason()
    {
        var store = new WorkItemStore(_db, new OrchestratorRunService(Mock.Of<Serilog.ILogger>()), Router());

        var (outcome, _, reason) = await store.CreateAsync(Request(WorkItemTaskType.Implementation, repo: "repo-unknown"), CancellationToken.None);

        outcome.Should().Be(WorkItemCreateOutcome.Refused);
        reason.Should().Be(AgentRouteRefusalReason.NoRouteText);
        await using var db = _db.CreateDbContext();
        db.WorkItems.Should().BeEmpty();
    }

    // ── The stored payload keeps the pinned route ────────────────────────────

    [Fact]
    public void BuildMinimalPayload_KeepsThePinnedRouteAndProfile()
    {
        var route = new AgentRouteSnapshot("claude", "Claude", JobTemplateProviderType.Claude, "opus", "high", AgentRouteSource.Repository);
        var request = Request(WorkItemTaskType.Implementation) with
        {
            AgentSelector = "claude,dotnet",
            ResolvedProfileId = "claude",
            Route = route,
            McpServers = [new McpServerConfig { Name = "mutable" }],
        };

        var minimal = WorkItemDispatchEndpoints.BuildMinimalPayload(request);

        minimal.Route.Should().Be(route);
        minimal.ResolvedProfileId.Should().Be("claude");
        minimal.AgentSelector.Should().Be("claude,dotnet");
        minimal.McpServers.Should().BeNullOrEmpty("mutable configuration is fetched fresh at claim");
    }
}
