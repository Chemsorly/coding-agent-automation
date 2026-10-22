using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.2a: run history and the work-item lists on the UI surface. Lists follow D3 rule 3:
/// without a project the user needs global read (403), with one, read on it (404). A run's detail
/// is checked against the run's stored project (T3, IDOR between projects R and S). The requests go
/// through the web's real <see cref="UiRunClient"/> and <see cref="UiWorkItemClient"/>, so these
/// tests double as their contract tests.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiRunEndpointTests(ApiWebApplicationFactory factory)
{
    // Not ProjectB: UiSurfaceTests expects that one to stay unknown.
    private const string ProjectR = "6f1c2a9e-0000-0000-0000-0000000000f1";
    private const string ProjectS = "6f1c2a9e-0000-0000-0000-0000000000f2";

    private static readonly AccessGrant ReaderOfR = TestUserTokens.Scoped(ProjectR, AccessRole.ReadOnly);
    private static readonly AccessGrant GlobalReader = TestUserTokens.Global(AccessRole.ReadOnly);
    private static readonly AccessGrant OperatorOfR = TestUserTokens.Scoped(ProjectR, AccessRole.Operator);

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiRunClient Runs(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private UiWorkItemClient WorkItems(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private async Task<PipelineRunSummary> SeedRunAsync(
        string? projectId, string? agentProfileName = null, string? modelName = null, string? agentEffort = null)
    {
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = $"ui-run-{Guid.NewGuid():N}",
            IssueTitle = "UI run",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = DateTimeOffset.UtcNow,
            ProjectId = projectId,
            AgentProfileName = agentProfileName,
            ModelName = modelName,
            AgentEffort = agentEffort,
        };
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPipelineRunHistoryService>().AddRunSummaryAsync(run, CancellationToken.None);
        return run;
    }

    private Guid SeedWorkItem(string? projectId, WorkItemStatus status)
    {
        var issue = $"ui-wi-{Guid.NewGuid():N}";
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier(issue),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            ProjectId = projectId is null ? null : Guid.Parse(projectId),
        };
        using var db = factory.CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issue,
            IssueProviderConfigId = "prov-1",
            Status = status,
            Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),
            DispatchedAt = status == WorkItemStatus.Pending ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
            ProjectId = projectId is null ? null : Guid.Parse(projectId),
        };
        db.WorkItems.Add(entity);
        db.SaveChanges();
        return entity.Id;
    }

    // ── D3 rule 3: lists ────────────────────────────────────────────────

    [Fact]
    public async Task Lists_WithoutAProject_NeedGlobalRead()
    {
        var scoped = Http(ReaderOfR);
        var global = Http(GlobalReader);

        foreach (var list in new[] { "/api/ui/runs", "/api/ui/work-items/pending", "/api/ui/work-items/active" })
        {
            (await scoped.GetAsync(list)).StatusCode.Should().Be(HttpStatusCode.Forbidden, list);
            (await global.GetAsync(list)).StatusCode.Should().Be(HttpStatusCode.OK, list);
        }
    }

    [IdorCase("GET /api/ui/runs")]
    [IdorCase("GET /api/ui/work-items/pending")]
    [IdorCase("GET /api/ui/work-items/active")]
    [Fact]
    public async Task Lists_OfAnotherProject_AreNotFound()
    {
        var scoped = Http(ReaderOfR);

        foreach (var list in new[] { "/api/ui/runs", "/api/ui/work-items/pending", "/api/ui/work-items/active" })
            (await scoped.GetAsync($"{list}?projectId={ProjectS}")).StatusCode.Should().Be(HttpStatusCode.NotFound, list);
    }

    [Fact]
    public async Task Lists_OfTheUsersProject_ContainOnlyThatProject()
    {
        var runOfR = await SeedRunAsync(ProjectR);
        var runOfS = await SeedRunAsync(ProjectS);
        var pendingOfR = SeedWorkItem(ProjectR, WorkItemStatus.Pending);
        var pendingOfS = SeedWorkItem(ProjectS, WorkItemStatus.Pending);
        var runningOfR = SeedWorkItem(ProjectR, WorkItemStatus.Running);
        var runningOfS = SeedWorkItem(ProjectS, WorkItemStatus.Running);

        var runs = await Runs(ReaderOfR).GetRunHistoryAsync(new RunHistoryQuery(PageSize: 100, ProjectId: ProjectR));
        var pending = await WorkItems(ReaderOfR).GetPendingAsync(100, ProjectR);
        var active = await WorkItems(ReaderOfR).GetActiveAsync(0, ProjectR);

        runs.Items.Select(r => r.RunId).Should().Contain(runOfR.RunId).And.NotContain(runOfS.RunId);
        runs.Items.Should().OnlyContain(r => r.ProjectId == ProjectR);
        runs.Items.Single(r => r.RunId == runOfR.RunId).IssueIdentifier.Value.Should().Be(runOfR.IssueIdentifier.Value);
        pending.Select(w => w.Id).Should().Contain(pendingOfR).And.NotContain(pendingOfS);
        active.Select(w => w.Id).Should().Contain(runningOfR).And.NotContain(runningOfS);
    }

    [Fact]
    public async Task WorkItemLists_AProjectThatIsNotAGuid_MatchesNothing()
    {
        // Such a scope used to be ignored, which returned every project's work items.
        SeedWorkItem(ProjectS, WorkItemStatus.Pending);
        SeedWorkItem(ProjectS, WorkItemStatus.Running);

        (await WorkItems(GlobalReader).GetPendingAsync(100, "not-a-guid")).Should().BeEmpty();
        (await WorkItems(GlobalReader).GetActiveAsync(0, "not-a-guid")).Should().BeEmpty();
    }

    // ── Run detail ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunDetail_NamesTheAgentProfile_Model_AndEffort()
    {
        // Spec 053 req 8.3: as the run recorded them.
        var run = await SeedRunAsync(ProjectR, "Kiro (team pool)", "claude-sonnet-4", "high");

        var detail = await Runs(ReaderOfR).GetRunAsync(Guid.Parse(run.RunId));

        detail.Should().NotBeNull();
        detail!.Run.RunId.Should().Be(run.RunId);
        detail.Agent.Should().Be(new UiRunAgent("Kiro (team pool)", "claude-sonnet-4", "high"));
    }

    [Fact]
    public async Task RunDetail_OfARunFromBeforeAgentRouting_HasOnlyTheModel()
    {
        var run = await SeedRunAsync(ProjectR, modelName: "auto");

        var detail = await Runs(ReaderOfR).GetRunAsync(Guid.Parse(run.RunId));

        detail!.Agent.Should().Be(new UiRunAgent(null, "auto", null));
    }

    [IdorCase("GET /api/ui/runs/{runId:guid}")]
    [Fact]
    public async Task RunDetail_OfAnotherProject_IsNotFound()
    {
        var runOfS = await SeedRunAsync(ProjectS);

        (await Runs(ReaderOfR).GetRunAsync(Guid.Parse(runOfS.RunId))).Should().BeNull();
        (await Http(ReaderOfR).GetAsync($"/api/ui/runs/{runOfS.RunId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunDetail_WithoutAProject_NeedsGlobalRead()
    {
        var unscoped = await SeedRunAsync(projectId: null);

        (await Runs(ReaderOfR).GetRunAsync(Guid.Parse(unscoped.RunId))).Should().BeNull();
        (await Runs(GlobalReader).GetRunAsync(Guid.Parse(unscoped.RunId))).Should().NotBeNull();
    }

    [Fact]
    public async Task RunDetail_OfAnUnknownRun_IsNotFound()
    {
        (await Runs(GlobalReader).GetRunAsync(Guid.NewGuid())).Should().BeNull();
    }

    // ── 2.2b: actions on one work item ──────────────────────────────────

    private WorkItemEntity Stored(Guid id)
    {
        using var db = factory.CreateDbContext();
        return db.WorkItems.AsQueryable().Single(w => w.Id == id);
    }

    [Fact]
    public async Task Cancel_ByAnOperatorOfTheItemsProject_CancelsIt()
    {
        var queued = SeedWorkItem(ProjectR, WorkItemStatus.Pending);

        await WorkItems(OperatorOfR).CancelAsync(queued);

        Stored(queued).Status.Should().Be(WorkItemStatus.Cancelled);
    }

    [IdorCase("POST /api/ui/work-items/{id:guid}/cancel")]
    [IdorCase("PUT /api/ui/work-items/{id:guid}/priority")]
    [Fact]
    public async Task Actions_OnAnotherProjectsItem_AreNotFound_AndChangeNothing()
    {
        var itemOfS = SeedWorkItem(ProjectS, WorkItemStatus.Pending);

        var cancel = () => WorkItems(OperatorOfR).CancelAsync(itemOfS);
        var priority = () => WorkItems(OperatorOfR).SetPriorityAsync(itemOfS, 900);

        (await cancel.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("404");
        (await priority.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("404");
        Stored(itemOfS).Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task Actions_WithReadOnlyAccessToTheItemsProject_AreForbidden()
    {
        // The user operates on R, so the endpoint's AnyOperator policy passes; on S they only read.
        var operatorOfRReaderOfS = new AccessGrant(AccessRole.None,
            new Dictionary<string, AccessRole> { [ProjectR] = AccessRole.Operator, [ProjectS] = AccessRole.ReadOnly }, []);
        var itemOfS = SeedWorkItem(ProjectS, WorkItemStatus.Pending);

        (await Http(operatorOfRReaderOfS).PostAsync($"/api/ui/work-items/{itemOfS}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(ReaderOfR).PostAsync($"/api/ui/work-items/{itemOfS}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        Stored(itemOfS).Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task Actions_OnAnItemWithoutAProject_NeedGlobalOperate()
    {
        var unscoped = SeedWorkItem(projectId: null, WorkItemStatus.Pending);

        var asOperatorOfR = () => WorkItems(OperatorOfR).CancelAsync(unscoped);

        await asOperatorOfR.Should().ThrowAsync<InvalidOperationException>();
        await WorkItems(TestUserTokens.Global(AccessRole.Operator)).SetPriorityAsync(unscoped, 10);
        Stored(unscoped).PriorityWeight.Should().Be(10);
    }

    [Fact]
    public async Task SetPriority_UpdatesAQueuedItem_AndARefusalCarriesTheReason()
    {
        var queued = SeedWorkItem(ProjectR, WorkItemStatus.Pending);
        var running = SeedWorkItem(ProjectR, WorkItemStatus.Running);

        await WorkItems(OperatorOfR).SetPriorityAsync(queued, 700);
        var outOfRange = () => WorkItems(OperatorOfR).SetPriorityAsync(queued, 1001);
        var notQueued = () => WorkItems(OperatorOfR).SetPriorityAsync(running, 700);

        Stored(queued).PriorityWeight.Should().Be(700);
        (await outOfRange.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("between 0 and 1000");
        (await notQueued.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("not in Pending status");
    }

    [Fact]
    public async Task Cancel_OfAnUnknownItem_IsNotFound()
    {
        var cancel = () => WorkItems(TestUserTokens.Global(AccessRole.Operator)).CancelAsync(Guid.NewGuid());

        await cancel.Should().ThrowAsync<InvalidOperationException>();
    }
}
