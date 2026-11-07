using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.UnitTests.Helpers;
using CodingAgent.Web.TestUtilities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

// ── Spec 056 DD1: CreateWorkItem never routes and never refuses for routing ──

/// <summary>
/// <see cref="WorkItemDispatchEndpoints.CreateWorkItem"/> inserts every request as a Pending work item without a route
/// (req 3.2): the dispatcher resolves the route when it dispatches the item and holds it when it cannot run. A route a
/// caller sent is dropped. This replaces spec 053's creation-time routing, its 422 and the consolidation
/// <c>template_missing</c> refusal (#3642).
/// </summary>
public sealed class CreateWorkItemRoutingTests
{
    private readonly string _dbName = $"create-work-item-routing-{Guid.NewGuid():N}";

    private IDbContextFactory<PipelineDbContext> CreateDbFactory()
    {
        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SimpleInMemoryDbContextFactory(opts);
    }

    private static IOrchestratorRunService CreateRunService() =>
        new OrchestratorRunService(Mock.Of<Serilog.ILogger>());

    private static JobDistributionRequest MakeRequest(WorkItemTaskType taskType) => new()
    {
        IssueIdentifier = new IssueIdentifier($"{taskType}:{Guid.NewGuid():N}"),
        IssueProviderConfigId = taskType == WorkItemTaskType.Consolidation ? ConsolidationConstants.ProviderConfigId : "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = taskType,
        TimeoutSeconds = 3600,
        RunId = Guid.NewGuid().ToString()
    };

    private Task<IResult> CreateAsync(
        JobDistributionRequest request, IOrchestratorRunService runService) =>
        WorkItemDispatchEndpoints.CreateWorkItem(
            request,
            new WorkItemStore(CreateDbFactory(), runService),
            CancellationToken.None);

    private async Task<int> CountWorkItemsAsync()
    {
        await using var db = await CreateDbFactory().CreateDbContextAsync();
        return await db.WorkItems.CountAsync();
    }

    // req 3.2: no route resolves, yet the item is created (the dispatcher holds it as no_route).
    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    [InlineData(WorkItemTaskType.Consolidation)]
    public async Task NoRoute_Returns201_AndInsertsTheRowAndRegistersTheRun(WorkItemTaskType taskType)
    {
        var runService = CreateRunService();
        var request = MakeRequest(taskType);

        var result = await CreateAsync(request, runService);

        result.Should().BeOfType<Created<Guid>>("creation never refuses for routing (spec 056 DD1)");
        (await CountWorkItemsAsync()).Should().Be(1);
        runService.GetRun(new RunId(request.RunId!)).Should().NotBeNull();
    }

    // req 3.2, 3.7: a route the caller sent is not stored; the dispatcher resolves it.
    [Fact]
    public async Task ARouteTheCallerSent_IsDropped()
    {
        await AgentRoutingTestData.SeedRoutingAsync(CreateDbFactory());
        var request = MakeRequest(WorkItemTaskType.Implementation) with
        {
            AgentSelector = "dotnet,kiro",
            ResolvedProfileId = "profile-kiro",
            Route = new AgentRouteSnapshot("profile-kiro", "profile-kiro", JobTemplateProviderType.Kiro, "opus", "high", AgentRouteSource.GlobalDefault),
        };

        var result = await CreateAsync(request, CreateRunService());

        result.Should().BeOfType<Created<Guid>>();
        await using var db = await CreateDbFactory().CreateDbContextAsync();
        var row = await db.WorkItems.SingleAsync();
        row.AgentSelector.Should().BeEmpty();
        row.HoldReason.Should().BeNull();
        var payload = JsonSerializer.Deserialize<JobDistributionRequest>(row.Payload!, PipelineJsonOptions.Default)!;
        payload.Route.Should().BeNull();
        payload.ResolvedProfileId.Should().BeNull();
    }
}

// ── File-scoped helper ────────────────────────────────────────────────────────

file sealed class SimpleInMemoryDbContextFactory(DbContextOptions<PipelineDbContext> opts)
    : IDbContextFactory<PipelineDbContext>
{
    public PipelineDbContext CreateDbContext() => new(opts);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
        Task.FromResult(new PipelineDbContext(opts));
}
