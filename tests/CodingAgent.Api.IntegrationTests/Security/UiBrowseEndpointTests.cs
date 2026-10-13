using System.Net;
using System.Net.Http.Headers;
using AwesomeAssertions;
using System.Text.Json;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.3b: the backlog on the UI surface. It follows D3 rule 3 like the run lists, and the
/// web's real <see cref="UiBrowseClient"/> reads it, so this doubles as its contract test.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiBrowseEndpointTests(ApiWebApplicationFactory factory)
{
    // Not ProjectB: UiSurfaceTests expects that one to stay unknown.
    private const string ProjectT = "6f1c2a9e-0000-0000-0000-0000000000a7";
    private const string ProjectU = "6f1c2a9e-0000-0000-0000-0000000000a8";
    private const string TemplateT = "7a000000-0000-0000-0000-0000000000a7";
    private const string IssueProviderT = "8b000000-0000-0000-0000-0000000000a7";

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private UiBrowseClient Browse(AccessGrant grant) => new(factory.CreateClient(), new FixedToken(TestUserTokens.For(grant)));

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    [Fact]
    public async Task Backlog_WithoutAProject_NeedsGlobalRead_AndAnotherProjectIsNotFound()
    {
        var readerOfT = Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        (await readerOfT.GetAsync("/api/ui/backlog")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await readerOfT.GetAsync($"/api/ui/backlog?projectId={ProjectU}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync("/api/ui/backlog")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Backlog_ListsTheProjectsIssues_WithTheirReadiness()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectT, Name = "Tango" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectT, new PipelineJobTemplate
        {
            Id = TemplateT, Name = "tango-api", IssueProviderId = IssueProviderT, RepoProviderId = "rp-browse", Enabled = true,
        }, CancellationToken.None);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IProviderConfigStore>().SaveProviderConfigAsync(new ProviderConfig
            {
                Id = IssueProviderT, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Tango issues",
            }, CancellationToken.None);

        var issues = new Mock<IIssueProvider>();
        var page = new PagedResult<IssueSummary>
        {
            Items =
            [
                new IssueSummary { Identifier = "7", Title = "Ready", Labels = ["bug"], Description = "", Url = "https://x/issues/7" },
                new IssueSummary { Identifier = "8", Title = "Parked", Labels = ["backlog"], Description = "", Url = "https://x/issues/8" },
            ],
            Page = 1, PageSize = 50, HasMore = false,
        };
        issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(page);
        issues.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>())).ReturnsAsync(page);
        Mock.Get(factory.Services.GetRequiredService<IProviderFactory>())
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == IssueProviderT)))
            .Returns(issues.Object);

        var backlog = await Browse(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).GetBacklogAsync(ProjectT);

        backlog.IsTruncated.Should().BeFalse();
        backlog.Issues.Select(i => (i.Identifier, i.IsReady)).Should().BeEquivalentTo([("7", true), ("8", false)]);
        backlog.Issues.Single(i => i.Identifier == "7").Labels.Should().Equal("bug");
        backlog.BlockedIssues().Should().BeEmpty("a parked issue is not ready, but nothing blocks it");
    }

    // ── Template browse (2.3b part 2) ─────────────────────────────────────

    private const string TemplateOfU = "7a000000-0000-0000-0000-0000000000a8";
    private const string TemplateWithoutProject = "7a000000-0000-0000-0000-0000000000a9";
    private const string RepoProviderT = "8b000000-0000-0000-0000-0000000000a8";

    private async Task SeedTemplatesAsync()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectT, Name = "Tango" }, CancellationToken.None);
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectU, Name = "Uniform" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectT, new PipelineJobTemplate
        {
            Id = TemplateT, Name = "tango-api", IssueProviderId = IssueProviderT, RepoProviderId = RepoProviderT, Enabled = true,
        }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectU, new PipelineJobTemplate
        {
            Id = TemplateOfU, Name = "uniform-api", IssueProviderId = IssueProviderT, RepoProviderId = RepoProviderT,
        }, CancellationToken.None);
        using var scope = factory.Services.CreateScope();
        var configs = scope.ServiceProvider.GetRequiredService<IProviderConfigStore>();
        await configs.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = IssueProviderT, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Tango issues",
        }, CancellationToken.None);
        await configs.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = RepoProviderT, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Tango repo",
        }, CancellationToken.None);
    }

    private Mock<IIssueProvider> IssuesOfT()
    {
        var issues = new Mock<IIssueProvider>();
        Mock.Get(factory.Services.GetRequiredService<IProviderFactory>())
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == IssueProviderT)))
            .Returns(issues.Object);
        return issues;
    }

    [Fact]
    public async Task TemplateBrowse_NeedsReadOnTheTemplatesProject()
    {
        await SeedTemplatesAsync();
        var readerOfT = Http(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        foreach (var path in new[] { "issues", "issues/readiness", "labels", "pull-requests", "epics", "epic-labels", "active-issues" })
        {
            (await readerOfT.GetAsync($"/api/ui/templates/{TemplateOfU}/{path}")).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
            (await readerOfT.GetAsync($"/api/ui/templates/{TemplateWithoutProject}/{path}")).StatusCode.Should().Be(HttpStatusCode.NotFound, path);
        }
        (await readerOfT.GetAsync($"/api/ui/templates/{TemplateT}/active-issues")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task TemplateBrowse_ListsTheTrackersIssues_ThroughTheClient()
    {
        await SeedTemplatesAsync();
        var issues = IssuesOfT();
        issues.Setup(p => p.ListOpenIssuesAsync(2, 15, It.Is<IReadOnlyList<string>?>(l => l != null && l.Count == 2 && l[0] == "bug" && l[1] == "a b"), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "7", Title = "Ready", Labels = ["bug"], Description = "", Url = "https://x/issues/7" }],
                Page = 2, PageSize = 15, HasMore = true,
            });
        issues.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["bug", "a b"]);
        var client = Browse(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        var page = await client.GetIssuesAsync(TemplateT, 2, ["bug", "a b"]);
        var labels = await client.GetLabelsAsync(TemplateT);

        page.Items.Single().Identifier.Should().Be("7");
        page.HasMore.Should().BeTrue();
        labels.Should().Equal("bug", "a b");
    }

    [Fact]
    public async Task TemplateBrowse_ReadinessPullRequestsAndEpics_RoundTripThroughTheClient()
    {
        await SeedTemplatesAsync();
        var issues = IssuesOfT();
        issues.Setup(p => p.ListOpenIssuesAsync(1, 15, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = [new IssueSummary { Identifier = "9", Title = "Blocked", Labels = [], Description = "Depends on #4" }],
                Page = 1, PageSize = 15, HasMore = false,
            });
        issues.Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
        issues.Setup(p => p.ListOpenIssuesAsync(1, 8, It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [new IssueSummary { Identifier = "30", Title = "Epic", Labels = [AgentLabels.Epic] }], Page = 1, PageSize = 8, HasMore = false });
        issues.Setup(p => p.ListOpenIssuesAsync(1, 8, It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 8, HasMore = false });
        issues.Setup(p => p.ListRepositoryLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(["backend", AgentLabels.Epic]);
        var repository = new Mock<IRepositoryProvider>();
        repository.Setup(r => r.ListOpenPullRequestsAsync(1, 15, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = [new PullRequestSummary { Identifier = "12", Number = 12, Title = "PR 12", BranchName = "feat/x", TargetBranch = "main", Url = "http://x", Description = "", Labels = [], IsDraft = true }],
                Page = 1, PageSize = 15, HasMore = false,
            });
        Mock.Get(factory.Services.GetRequiredService<IProviderFactory>())
            .Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == RepoProviderT)))
            .Returns(repository.Object);
        var client = Browse(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly));

        var readiness = await client.GetIssueReadinessAsync(TemplateT, 1, null);
        var pullRequests = await client.GetPullRequestsAsync(TemplateT, 1, null);
        var epics = await client.GetEpicsAsync(TemplateT, 1, null);
        var epicLabels = await client.GetEpicLabelsAsync(TemplateT);

        readiness["9"].IsReady.Should().BeFalse();
        readiness["9"].BlockedBy.Should().Equal(4);
        pullRequests.Items.Single().IsDraft.Should().BeTrue();
        epics.Items.Select(e => e.Identifier).Should().Equal("30");
        epicLabels.Should().Equal("backend");
    }

    [Fact]
    public async Task TemplateBrowse_AFailingProvider_ReachesThePageAsItsReason()
    {
        await SeedTemplatesAsync();
        IssuesOfT().Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));

        var list = () => Browse(TestUserTokens.Global(AccessRole.ReadOnly)).GetIssuesAsync(TemplateT, 1, null);

        (await list.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be("Failed to load issues: rate limited");
    }

    [Fact]
    public async Task ActiveIssues_BadgeTheTemplatesQueuedAndRunningWork()
    {
        await SeedTemplatesAsync();
        var queued = SeedWorkItem(IssueProviderT, WorkItemStatus.Pending);
        var running = SeedWorkItem(RepoProviderT, WorkItemStatus.Running);
        var otherProvider = SeedWorkItem("8b000000-0000-0000-0000-0000000000ff", WorkItemStatus.Running);

        var active = await Browse(TestUserTokens.Scoped(ProjectT, AccessRole.ReadOnly)).GetActiveIssuesAsync(TemplateT);

        active.Should().Contain(new UiActiveIssue(queued, IssueProviderT, WorkItemStatus.Pending));
        active.Should().Contain(new UiActiveIssue(running, RepoProviderT, WorkItemStatus.Running));
        active.Select(a => a.IssueIdentifier).Should().NotContain(otherProvider);
    }

    private string SeedWorkItem(string providerId, WorkItemStatus status)
    {
        var issue = $"browse-{Guid.NewGuid():N}";
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier(issue),
            IssueProviderConfigId = providerId,
            RepoProviderConfigId = RepoProviderT,
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600,
        };
        using var db = factory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issue,
            IssueProviderConfigId = providerId,
            Status = status,
            Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.SaveChanges();
        return issue;
    }
}
