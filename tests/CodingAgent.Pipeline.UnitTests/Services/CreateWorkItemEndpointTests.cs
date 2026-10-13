using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

// ── Issue #3642: CreateWorkItem refuses a consolidation run that has no job template ──

/// <summary>
/// <c>POST /api/work-items</c> used to enqueue every task type as Pending without looking at job templates. A
/// consolidation run whose agent selector has no template then sat Pending for ever (the dispatcher answers
/// 200/deferred <c>no_template</c> every cycle). <see cref="WorkItemDispatchEndpoints.CreateWorkItem"/> now
/// answers 422 for such a Consolidation request, before any row or in-memory run exists. The template is resolved
/// exactly as the dispatcher does (direct lookup, then the profile fallback), so a run the dispatcher can
/// dispatch is never refused. Other task types are not checked.
/// </summary>
public sealed class CreateWorkItemConsolidationTemplateGateTests
{
    private readonly string _dbName = $"create-work-item-template-gate-{Guid.NewGuid():N}";

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

    private static JobTemplateStore CreateTemplateStore(string labels = "kiro,dotnet") =>
        JobTemplateStore.LoadFromYaml($"""
            - labels: "{labels}"
              image: "test-image:latest"
              imagePullPolicy: "Always"
              providerType: "kiro"
              maxConcurrent: 5
            """);

    private static JobDistributionRequest MakeRequest(WorkItemTaskType taskType, string selector) => new()
    {
        IssueIdentifier = new IssueIdentifier($"{taskType}:{Guid.NewGuid():N}"),
        IssueProviderConfigId = taskType == WorkItemTaskType.Consolidation ? ConsolidationConstants.ProviderConfigId : "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = taskType,
        AgentSelector = selector,
        TimeoutSeconds = 3600,
        RunId = Guid.NewGuid().ToString()
    };

    private static Mock<IAgentProfileStore> CreateProfileStore(params string[] matchLabels)
    {
        var store = new Mock<IAgentProfileStore>();
        store.Setup(ps => ps.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new AgentProfile
                {
                    DisplayName = "Kiro+Dotnet",
                    AgentProviderConfigId = "agent-kiro",
                    MatchLabels = matchLabels.ToList(),
                    Enabled = true
                }
            });
        return store;
    }

    private Task<IResult> CreateAsync(
        JobDistributionRequest request,
        IOrchestratorRunService runService,
        JobTemplateStore templateStore,
        IAgentProfileStore? profileStore = null) =>
        WorkItemDispatchEndpoints.CreateWorkItem(
            request,
            new WorkItemStore(CreateDbFactory(), runService),
            new DispatchWorkItemService(templateStore),
            new DispatchTemplateResolver(profileStore, templateStore),
            CancellationToken.None);

    private async Task<int> CountWorkItemsAsync()
    {
        await using var db = await CreateDbFactory().CreateDbContextAsync();
        return await db.WorkItems.CountAsync();
    }

    private static void ShouldBe422(IResult result)
    {
        var status = result as StatusCodeHttpResult;
        status.Should().NotBeNull("a consolidation run without a job template is refused with a bare status code");
        status!.StatusCode.Should().Be(StatusCodes.Status422UnprocessableEntity);
    }

    // ── 422: no template, no row, no run ─────────────────────────────────────

    [Fact]
    public async Task Consolidation_NoTemplateForSelector_Returns422_AndInsertsNoRowAndRegistersNoRun()
    {
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Consolidation, "kiro,python,python312");

        // The store only has kiro,dotnet — nothing for the requested selector.
        var result = await CreateAsync(request, runService, CreateTemplateStore("kiro,dotnet"));

        ShouldBe422(result);
        (await CountWorkItemsAsync()).Should().Be(0, "the refusal comes before any row is inserted");
        runService.GetRun(new RunId(request.RunId!)).Should().BeNull("no in-memory run may be registered for a refused request");
        runService.GetActiveRuns().Should().BeEmpty();
    }

    [Fact]
    public async Task Consolidation_EmptySelector_Returns422()
    {
        // No labels means no template can match, directly or through a profile: the run could never be dispatched.
        var result = await CreateAsync(
            MakeRequest(WorkItemTaskType.Consolidation, ""), CreateRunService(), CreateTemplateStore());

        ShouldBe422(result);
        (await CountWorkItemsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Consolidation_ProfileDoesNotCoverSelector_Returns422()
    {
        // A profile exists, but its labels do not cover the selector, so the fallback finds no template either.
        var profiles = CreateProfileStore("kiro", "dotnet");

        var result = await CreateAsync(
            MakeRequest(WorkItemTaskType.Consolidation, "opencode,java"), CreateRunService(),
            CreateTemplateStore("kiro,dotnet"), profiles.Object);

        ShouldBe422(result);
        (await CountWorkItemsAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Consolidation_ProfileCoversSelectorButItsTemplateIsMissing_Returns422()
    {
        // The profile expands "dotnet" to "dotnet,kiro", but no template is keyed on that either.
        var profiles = CreateProfileStore("dotnet", "kiro");

        var result = await CreateAsync(
            MakeRequest(WorkItemTaskType.Consolidation, "dotnet"), CreateRunService(),
            CreateTemplateStore("opencode,java"), profiles.Object);

        ShouldBe422(result);
        (await CountWorkItemsAsync()).Should().Be(0);
    }

    // ── 201: a template exists ───────────────────────────────────────────────

    [Theory]
    [InlineData("kiro,dotnet")]
    [InlineData("dotnet,kiro")] // the template key is normalized, so label order does not matter
    public async Task Consolidation_DirectTemplate_Returns201_AndInsertsTheRow(string selector)
    {
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Consolidation, selector);

        var result = await CreateAsync(request, runService, CreateTemplateStore("kiro,dotnet"));

        var created = result as Created<Guid>;
        created.Should().NotBeNull("a consolidation run with a job template is enqueued as before");
        created!.Value.Should().Be(Guid.Parse(request.RunId!));
        (await CountWorkItemsAsync()).Should().Be(1);
        runService.GetRun(new RunId(request.RunId!)).Should().NotBeNull("the run is registered so the Consolidation page shows it");
    }

    [Fact]
    public async Task Consolidation_ProfileFallbackFindsTemplate_Returns201()
    {
        // Selector "dotnet" is a partial subset of the profile's MatchLabels ["dotnet","kiro"],
        // which maps to the "kiro,dotnet" template — so the run is allowed.
        var profiles = CreateProfileStore("dotnet", "kiro");
        var runService = CreateRunService();
        var request = MakeRequest(WorkItemTaskType.Consolidation, "dotnet");

        var result = await CreateAsync(request, runService, CreateTemplateStore("kiro,dotnet"), profiles.Object);

        var created = result as Created<Guid>;
        created.Should().NotBeNull("the profile fallback found a template, so the run is enqueued");
        (await CountWorkItemsAsync()).Should().Be(1);
    }

    // ── Other task types: template check is skipped ──────────────────────────

    [Theory]
    [InlineData(WorkItemTaskType.Implementation)]
    [InlineData(WorkItemTaskType.Review)]
    [InlineData(WorkItemTaskType.Decomposition)]
    [InlineData(WorkItemTaskType.Triage)]
    public async Task NonConsolidationTaskTypes_WithNoMatchingTemplate_StillReturn201(WorkItemTaskType taskType)
    {
        // The template store has "kiro,dotnet" but the request uses a selector with no matching template.
        // Non-consolidation task types are not checked at enqueue: they follow the pending-dispatch path.
        var runService = CreateRunService();
        var request = MakeRequest(taskType, "kiro,python,python312");

        // Store has kiro,dotnet only — no match for kiro,python,python312
        var result = await CreateAsync(request, runService, CreateTemplateStore("kiro,dotnet"));

        var created = result as Created<Guid>;
        created.Should().NotBeNull($"{taskType} must be enqueued as Pending regardless of template availability");
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
