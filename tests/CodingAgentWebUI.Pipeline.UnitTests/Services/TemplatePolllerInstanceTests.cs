using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TemplatePoller"/> instance methods:
/// <see cref="TemplatePoller.PollProjectLevelEpicsAsync"/> and the private
/// PollSingleProjectEpicsAsync, exercised by pre-populating the ProviderCacheManager.
/// </summary>
public class TemplatePolllerInstanceTests
{
    private static TemplatePoller CreatePoller(
        Dictionary<string, IIssueProvider>? issueProviders = null,
        Dictionary<string, IRepositoryProvider>? repoProviders = null)
    {
        var mockFactory = new Mock<IProviderFactory>();
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(mockFactory.Object, logger);

        if (issueProviders is not null)
            foreach (var kvp in issueProviders)
                cacheManager.IssueProviders[kvp.Key] = kvp.Value;

        if (repoProviders is not null)
            foreach (var kvp in repoProviders)
                cacheManager.RepoProviders[kvp.Key] = kvp.Value;

        return new TemplatePoller(cacheManager, logger);
    }

    private static PipelineProject MakeProject(
        string id, string epicProviderId, IReadOnlyList<string>? templateIds = null) =>
        new()
        {
            Id = id,
            Name = $"Project-{id}",
            Enabled = true,
            EpicIssueProviderId = epicProviderId,
            TemplateIds = templateIds ?? []
        };

    private static PipelineJobTemplate MakeTemplate(string id, bool enabled = true, bool decompositionEnabled = true) =>
        new()
        {
            Id = id,
            Name = $"Template-{id}",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = enabled,
            DecompositionEnabled = decompositionEnabled
        };

    private static IssueSummary MakeIssue(string id, string[]? labels = null) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = labels ?? [] };

    private static PagedResult<IssueSummary> EmptyPage() =>
        new() { Items = [], Page = 1, PageSize = 25, HasMore = false };

    private static PagedResult<IssueSummary> SinglePage(IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 25, HasMore = false };

    // ── PollProjectLevelEpicsAsync — no projects ──────────────────────────────

    [Fact]
    public async Task PollProjectLevelEpicsAsync_NoProjects_ReturnsEmptyDictionary()
    {
        var poller = CreatePoller();
        var result = await poller.PollProjectLevelEpicsAsync(
            Array.Empty<PipelineProject>(), new Dictionary<string, PipelineJobTemplate>(), 3, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_DisabledProject_SkipsIt()
    {
        var project = new PipelineProject
        {
            Id = "p1", Name = "P1", Enabled = false,
            EpicIssueProviderId = "ep-1", TemplateIds = []
        };
        var poller = CreatePoller();
        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, CancellationToken.None);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_NullEpicIssueProviderId_SkipsIt()
    {
        var project = new PipelineProject
        {
            Id = "p1", Name = "P1", Enabled = true,
            EpicIssueProviderId = null, TemplateIds = []
        };
        var poller = CreatePoller();
        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, CancellationToken.None);
        result.Should().BeEmpty();
    }

    // ── PollSingleProjectEpicsAsync — provider not in cache ──────────────────

    [Fact]
    public async Task PollProjectLevelEpicsAsync_EpicProviderNotInCache_SkipsProject()
    {
        var project = MakeProject("p1", "missing-provider");
        var poller = CreatePoller(); // empty cache
        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, CancellationToken.None);
        result.Should().BeEmpty();
    }

    // ── PollSingleProjectEpicsAsync — no decomposition template ──────────────

    [Fact]
    public async Task PollProjectLevelEpicsAsync_NoDecompositionTemplate_SkipsProject()
    {
        var epicProvider = new Mock<IIssueProvider>().Object;
        var project = MakeProject("p1", "ep-1", ["t1"]);
        var template = MakeTemplate("t1", enabled: false, decompositionEnabled: true);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = epicProvider });

        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, CancellationToken.None);

        result.Should().BeEmpty();
    }

    // ── PollSingleProjectEpicsAsync — success, empty result ──────────────────

    [Fact]
    public async Task PollProjectLevelEpicsAsync_EmptyIssues_ReturnsEmptyQueue()
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());

        var project = MakeProject("p1", "ep-1", ["t1"]);
        var template = MakeTemplate("t1");
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });

        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, CancellationToken.None);

        // No items — nothing added to the queue
        result.Should().BeEmpty();
    }

    // ── PollSingleProjectEpicsAsync — success with epic issues ───────────────

    [Fact]
    public async Task PollProjectLevelEpicsAsync_EpicIssues_PopulatesDecompositionAnalysisQueue()
    {
        var epicIssue = MakeIssue("epic-1", [AgentLabels.Epic]);
        var mockProvider = new Mock<IIssueProvider>();

        // First call: agent:epic label → returns epic
        // Second call: agent:epic-approved label → empty
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePage([epicIssue]));
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });

        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, CancellationToken.None);

        result.Should().ContainKey("p1");
        result["p1"].Should().HaveCount(1);
        result["p1"][0].Phase.Should().Be(PipelineRunType.DecompositionAnalysis);
        result["p1"][0].Issue.Identifier.Should().Be("epic-1");
        result["p1"][0].Template.Id.Should().Be("t1");
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_ApprovedIssues_PopulatesDecompositionQueue()
    {
        var approvedIssue = MakeIssue("approved-1", [AgentLabels.EpicApproved]);
        var mockProvider = new Mock<IIssueProvider>();

        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePage([approvedIssue]));

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });

        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, CancellationToken.None);

        result.Should().ContainKey("p1");
        result["p1"].Should().HaveCount(1);
        result["p1"][0].Phase.Should().Be(PipelineRunType.Decomposition);
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_ProviderThrows_SwallowsExceptionAndSkipsProject()
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });

        // Should not throw
        var act = () => poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_CancellationRequestedBeforeLoop_ReturnsEmptyEarly()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var mockProvider = new Mock<IIssueProvider>(); // never called
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });

        // Already cancelled — loop breaks immediately
        var result = await poller.PollProjectLevelEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, cts.Token);

        result.Should().BeEmpty("cancellation before loop causes immediate break");
        mockProvider.Verify(p => p.ListOpenIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task PollProjectLevelEpicsAsync_MultipleProjects_ProcessesAll()
    {
        var mockProvider1 = new Mock<IIssueProvider>();
        var mockProvider2 = new Mock<IIssueProvider>();
        mockProvider1
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePage([MakeIssue("e1", [AgentLabels.Epic])]));
        mockProvider1
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());
        mockProvider2
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());

        var template1 = MakeTemplate("t1");
        var template2 = MakeTemplate("t2");
        var project1 = MakeProject("p1", "ep-1", [template1.Id]);
        var project2 = MakeProject("p2", "ep-2", [template2.Id]);

        var poller = CreatePoller(issueProviders: new()
        {
            ["ep-1"] = mockProvider1.Object,
            ["ep-2"] = mockProvider2.Object
        });

        var lookup = new Dictionary<string, PipelineJobTemplate>
        {
            [template1.Id] = template1,
            [template2.Id] = template2
        };

        var result = await poller.PollProjectLevelEpicsAsync(
            [project1, project2], lookup, 3, CancellationToken.None);

        // p1 had an epic, p2 was empty so not added
        result.Should().ContainKey("p1");
        result.Should().NotContainKey("p2");
        result["p1"].Should().HaveCount(1);
    }
}
