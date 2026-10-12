using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
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

    private async Task<PipelineRunSummary> SeedRunAsync(string? projectId, string? agentProviderConfigId = null)
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
            AgentProviderConfigId = agentProviderConfigId,
        };
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IPipelineRunHistoryService>().AddRunSummaryAsync(run, CancellationToken.None);
        return run;
    }

    private Guid SeedWorkItem(string projectId, WorkItemStatus status)
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
            ProjectId = Guid.Parse(projectId),
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
            ProjectId = Guid.Parse(projectId),
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
    public async Task RunDetail_NamesTheAgentProvider()
    {
        var provider = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(), Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro (team pool)",
        };
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(provider, CancellationToken.None);
        var run = await SeedRunAsync(ProjectR, provider.Id);

        var detail = await Runs(ReaderOfR).GetRunAsync(Guid.Parse(run.RunId));

        detail.Should().NotBeNull();
        detail!.Run.RunId.Should().Be(run.RunId);
        detail.AgentProviderName.Should().Be("Kiro (team pool)");
    }

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
}
