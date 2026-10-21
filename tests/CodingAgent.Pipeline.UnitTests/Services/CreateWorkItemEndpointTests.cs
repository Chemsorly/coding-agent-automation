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

// ── Spec 053 / issue #3642: CreateWorkItem routes the request, and refuses what cannot run ──

/// <summary>
/// <see cref="WorkItemDispatchEndpoints.CreateWorkItem"/> routes every request when it creates the work item (spec 053
/// DD1): the row carries the routed profile's job template as its selector and the route in its payload. A request no
/// route resolves for is answered 422 with the reason as ProblemDetails <c>detail</c>, before any row or in-memory run
/// exists (D15). A consolidation run whose routed profile has no job template is refused the same way, because the
/// dispatcher could never start it (#3642); other task types are not checked against the job templates.
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
        JobDistributionRequest request, IOrchestratorRunService runService, JobTemplateStore? jobTemplates = null) =>
        WorkItemDispatchEndpoints.CreateWorkItem(
            request,
            new WorkItemStore(CreateDbFactory(), runService, TestWorkItemRouter.Create(CreateDbFactory(), jobTemplates)),
            CancellationToken.None);

    private async Task<int> CountWorkItemsAsync()
    {
        await using var db = await CreateDbFactory().CreateDbContextAsync();
        return await db.WorkItems.CountAsync();
    }

    private static string ShouldBeRefused(IResult result)
    {
        var problem = result as ProblemHttpResult;
        problem.Should().NotBeNull("a refused request is answered with ProblemDetails");
        problem!.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
        return problem.ProblemDetails.Detail!;
    }

    // ── 422: no route, no row, no run (D15) ──────────────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    [InlineData(WorkItemTaskType.Consolidation)]
    public async Task NoRoute_Returns422WithTheReason_AndInsertsNoRowAndRegistersNoRun(WorkItemTaskType taskType)
    {
        var runService = CreateRunService();
        var request = MakeRequest(taskType);

        var result = await CreateAsync(request, runService);

        ShouldBeRefused(result).Should().Be(AgentRouteRefusalReason.NoRouteText);
        (await CountWorkItemsAsync()).Should().Be(0, "the refusal comes before any row is inserted");
        runService.GetRun(new RunId(request.RunId!)).Should().BeNull("no in-memory run may be registered for a refused request");
        runService.GetActiveRuns().Should().BeEmpty();
    }

    // ── 422: a consolidation run whose job template is missing ───────────────

    [Fact]
    public async Task Consolidation_RoutedProfileHasNoJobTemplate_Returns422_AndInsertsNoRow()
    {
        await AgentRoutingTestData.SeedRoutingAsync(CreateDbFactory());
        var runService = CreateRunService();

        // The routed profile runs on "dotnet,kiro"; the Helm values only have "java,opencode".
        var result = await CreateAsync(
            MakeRequest(WorkItemTaskType.Consolidation), runService,
            AgentRoutingTestData.JobTemplates(("java,opencode", JobTemplateProviderType.Opencode)));

        ShouldBeRefused(result).Should().Contain("dotnet,kiro").And.Contain("does not exist in the Helm values");
        (await CountWorkItemsAsync()).Should().Be(0);
        runService.GetActiveRuns().Should().BeEmpty();
    }

    // ── 201: routed ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Routed_Returns201_AndStoresTheProfilesJobTemplateAndTheRoute()
    {
        await AgentRoutingTestData.SeedRoutingAsync(
            CreateDbFactory(), AgentRoutingTestData.Routing(model: "claude-opus-4", effort: "high"));
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Consolidation);

        var result = await CreateAsync(request, runService);

        var created = result as Created<Guid>;
        created.Should().NotBeNull("a routed consolidation run with a job template is enqueued");
        created!.Value.Should().Be(Guid.Parse(request.RunId!));
        runService.GetRun(new RunId(request.RunId!)).Should().NotBeNull("the run is registered so the Consolidation page shows it");

        await using var db = await CreateDbFactory().CreateDbContextAsync();
        var row = await db.WorkItems.SingleAsync();
        row.AgentSelector.Should().Be(AgentRoutingTestData.DefaultJobTemplate);
        var payload = JsonSerializer.Deserialize<JobDistributionRequest>(row.Payload!, PipelineJsonOptions.Default)!;
        payload.ResolvedProfileId.Should().Be("profile-kiro");
        payload.Route.Should().BeEquivalentTo(new AgentRouteSnapshot(
            "profile-kiro", "profile-kiro", JobTemplateProviderType.Kiro, "claude-opus-4", "high", AgentRouteSource.GlobalDefault));
    }

    // ── Other task types: the job template is not checked at creation ────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    public async Task NonConsolidationTaskTypes_WithTheJobTemplateMissing_StillReturn201(WorkItemTaskType taskType)
    {
        // They follow the pending-dispatch path, which defers work without a job template and alerts.
        await AgentRoutingTestData.SeedRoutingAsync(CreateDbFactory());

        var result = await CreateAsync(
            MakeRequest(taskType), CreateRunService(),
            AgentRoutingTestData.JobTemplates(("java,opencode", JobTemplateProviderType.Opencode)));

        result.Should().BeOfType<Created<Guid>>($"{taskType} must be enqueued as Pending regardless of template availability");
        (await CountWorkItemsAsync()).Should().Be(1);
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
