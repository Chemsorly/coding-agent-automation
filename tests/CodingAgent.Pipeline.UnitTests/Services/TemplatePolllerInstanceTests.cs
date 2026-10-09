using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="TemplatePoller"/> instance methods:
/// <see cref="TemplatePoller.AddProjectEpicsAsync"/> and the private
/// AddSingleProjectEpicsAsync, exercised by pre-populating the ProviderCacheManager.
/// Also covers the Phase 1 and Phase 2 eligibility filters of the private static FetchEpicIssuesAsync,
/// which AddSingleProjectEpicsAsync calls.
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

    private static IssueSummary MakeIssue(string id, string[]? labels = null, DateTime? createdAt = null) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = labels ?? [], CreatedAt = createdAt };

    private static PagedResult<IssueSummary> EmptyPage() =>
        new() { Items = [], Page = 1, PageSize = 25, HasMore = false };

    private static PagedResult<IssueSummary> SinglePage(IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 25, HasMore = false };

    private static Dictionary<string, List<EpicCandidate>> EmptyQueues() => new();

    /// <summary>Queues as PollTemplateQueuesAsync leaves them: one (empty) queue per template polled this cycle.</summary>
    private static Dictionary<string, List<EpicCandidate>> PolledQueues(params string[] polledTemplateIds) =>
        polledTemplateIds.ToDictionary(id => id, _ => new List<EpicCandidate>());

    /// <summary>An epic provider whose agent:epic list returns <paramref name="epics"/> and whose agent:epic-approved list is empty.</summary>
    private static Mock<IIssueProvider> EpicProvider(params IssueSummary[] epics)
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePage(epics));
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());
        return mockProvider;
    }

    /// <summary>An epic provider whose agent:epic list is empty and whose agent:epic-approved list returns <paramref name="approved"/>.</summary>
    private static Mock<IIssueProvider> ApprovedEpicProvider(params IssueSummary[] approved)
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.Epic)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, It.IsAny<int>(), It.Is<string[]>(l => l.Contains(AgentLabels.EpicApproved)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePage(approved));
        return mockProvider;
    }

    // ── AddProjectEpicsAsync — projects that are skipped ─────────────────────

    [Fact]
    public async Task AddProjectEpicsAsync_NoProjects_LeavesQueuesEmpty()
    {
        var poller = CreatePoller();
        var queues = EmptyQueues();

        await poller.AddProjectEpicsAsync(
            Array.Empty<PipelineProject>(), new Dictionary<string, PipelineJobTemplate>(), 3, queues, CancellationToken.None);

        queues.Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_DisabledProject_SkipsIt()
    {
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "P1",
            Enabled = false,
            EpicIssueProviderId = "ep-1",
            TemplateIds = []
        };
        var poller = CreatePoller();
        var queues = EmptyQueues();

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, queues, CancellationToken.None);

        queues.Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_NullEpicIssueProviderId_SkipsIt()
    {
        var project = new PipelineProject
        {
            Id = "p1",
            Name = "P1",
            Enabled = true,
            EpicIssueProviderId = null,
            TemplateIds = []
        };
        var poller = CreatePoller();
        var queues = EmptyQueues();

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, queues, CancellationToken.None);

        queues.Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_EpicProviderNotInCache_SkipsProject()
    {
        var project = MakeProject("p1", "missing-provider");
        var poller = CreatePoller(); // empty cache
        var queues = EmptyQueues();

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate>(), 3, queues, CancellationToken.None);

        queues.Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_NoDecompositionTemplate_SkipsProject()
    {
        var epicProvider = new Mock<IIssueProvider>().Object;
        var project = MakeProject("p1", "ep-1", ["t1"]);
        var template = MakeTemplate("t1", enabled: false, decompositionEnabled: true);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = epicProvider });
        var queues = EmptyQueues();

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues.Should().BeEmpty();
    }

    // ── AddProjectEpicsAsync — epics join the executor template's queue ──────

    [Fact]
    public async Task AddProjectEpicsAsync_EmptyIssues_AddsNothing()
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());

        var project = MakeProject("p1", "ep-1", ["t1"]);
        var template = MakeTemplate("t1");
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1");

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues["t1"].Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_EpicIssues_JoinExecutorQueueBoundToEpicTracker()
    {
        var mockProvider = EpicProvider(MakeIssue("epic-1", [AgentLabels.Epic]));
        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1");

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues.Should().ContainKey("t1");
        queues["t1"].Should().ContainSingle();
        queues["t1"][0].Phase.Should().Be(PipelineRunType.DecompositionAnalysis);
        queues["t1"][0].Issue.Identifier.Should().Be("epic-1");
        queues["t1"][0].IssueProviderId.Should().Be("ep-1", "the run is bound to the tracker the epic lives in");
    }

    [Fact]
    public async Task AddProjectEpicsAsync_ApprovedIssues_QueueDecompositionPhase()
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
        var queues = PolledQueues("t1");

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues["t1"].Should().ContainSingle();
        queues["t1"][0].Phase.Should().Be(PipelineRunType.Decomposition);
    }

    // ── AddProjectEpicsAsync — eligibility filters of FetchEpicIssuesAsync ──
    // Expected rules: docs/internals/decomposition-implementation.md, "Eligibility Filters".

    /// <summary>
    /// Phase 1: an agent:epic issue that also carries agent:epic-review (its plan awaits human approval),
    /// agent:in-progress, agent:error or agent:done is not queued, while a clean epic next to it is.
    /// </summary>
    [Theory]
    [InlineData(AgentLabels.EpicReview)]
    [InlineData(AgentLabels.InProgress)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.Done)]
    public async Task AddProjectEpicsAsync_Phase1EpicWithExclusionLabel_IsNotQueued(string exclusionLabel)
    {
        var mockProvider = EpicProvider(
            MakeIssue("epic-clean", [AgentLabels.Epic]),
            MakeIssue("epic-excluded", [AgentLabels.Epic, exclusionLabel]));
        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1");

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues["t1"].Should().ContainSingle(
            "an agent:epic issue that also carries {0} must not be queued for decomposition analysis", exclusionLabel);
        queues["t1"][0].Issue.Identifier.Should().Be("epic-clean");
        queues["t1"][0].Phase.Should().Be(PipelineRunType.DecompositionAnalysis);
    }

    /// <summary>
    /// Phase 2: an agent:epic-approved issue that also carries agent:in-progress, agent:error or
    /// agent:done is not queued, while a clean approved epic next to it is.
    /// </summary>
    [Theory]
    [InlineData(AgentLabels.InProgress)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.Done)]
    public async Task AddProjectEpicsAsync_Phase2ApprovedEpicWithExclusionLabel_IsNotQueued(string exclusionLabel)
    {
        var mockProvider = ApprovedEpicProvider(
            MakeIssue("approved-clean", [AgentLabels.EpicApproved]),
            MakeIssue("approved-excluded", [AgentLabels.EpicApproved, exclusionLabel]));
        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1");

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues["t1"].Should().ContainSingle(
            "an agent:epic-approved issue that also carries {0} must not be queued for decomposition", exclusionLabel);
        queues["t1"][0].Issue.Identifier.Should().Be("approved-clean");
        queues["t1"][0].Phase.Should().Be(PipelineRunType.Decomposition);
    }

    [Fact]
    public async Task AddProjectEpicsAsync_MergesWithExecutorsOwnEpics_OldestFirst()
    {
        var mockProvider = EpicProvider(MakeIssue("project-epic", [AgentLabels.Epic], new DateTime(2026, 1, 1)));
        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = EmptyQueues();
        queues["t1"] = [new EpicCandidate(MakeIssue("repo-epic", [AgentLabels.Epic], new DateTime(2026, 1, 2)), PipelineRunType.DecompositionAnalysis, "ip-1")];

        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        queues["t1"].Select(c => (c.Issue.Identifier, c.IssueProviderId)).Should().Equal(
            ("project-epic", "ep-1"),
            ("repo-epic", "ip-1"));
    }

    [Fact]
    public async Task AddProjectEpicsAsync_EpicTrackerIsATemplatesTracker_QueuesEachEpicOnceAsProjectEpic()
    {
        var epic = MakeIssue("epic-1", [AgentLabels.Epic]);
        var mockProvider = EpicProvider(epic);
        var executor = MakeTemplate("t1");
        var trackerOwner = MakeTemplate("t2") with { IssueProviderId = "ep-1" };
        var project = MakeProject("p1", "ep-1", [executor.Id, trackerOwner.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = EmptyQueues();
        queues["t1"] = [];
        queues["t2"] = [new EpicCandidate(epic, PipelineRunType.DecompositionAnalysis, "ep-1")]; // t2's own poll of the same tracker

        var lookup = new Dictionary<string, PipelineJobTemplate> { [executor.Id] = executor, [trackerOwner.Id] = trackerOwner };
        await poller.AddProjectEpicsAsync([project], lookup, 3, queues, CancellationToken.None);

        queues["t2"].Should().BeEmpty("the epic tracker's epics are project epics, run by the executor");
        queues["t1"].Should().ContainSingle()
            .Which.IssueProviderId.Should().Be("ep-1");
    }

    [Fact]
    public async Task AddProjectEpicsAsync_ExecutorNotPolled_EpicsWaitAndTheTrackerOwnersCopiesAreDropped()
    {
        // The executor t1 was not polled this cycle (for example rate-limited), so it has no queue.
        // t2, whose own tracker is the epic tracker, must not run the project's epics in its place.
        var epic = MakeIssue("epic-1", [AgentLabels.Epic]);
        var mockProvider = EpicProvider(epic);
        var executor = MakeTemplate("t1");
        var trackerOwner = MakeTemplate("t2") with { IssueProviderId = "ep-1" };
        var project = MakeProject("p1", "ep-1", [executor.Id, trackerOwner.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t2");
        queues["t2"].Add(new EpicCandidate(epic, PipelineRunType.DecompositionAnalysis, "ep-1"));

        var lookup = new Dictionary<string, PipelineJobTemplate> { [executor.Id] = executor, [trackerOwner.Id] = trackerOwner };
        await poller.AddProjectEpicsAsync([project], lookup, 3, queues, CancellationToken.None);

        queues.Should().NotContainKey("t1", "the scheduler would never dispatch a queue for a template it did not poll");
        queues["t2"].Should().BeEmpty();
        mockProvider.Verify(p => p.ListOpenIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddProjectEpicsAsync_PollFails_TheTrackerOwnersCopiesAreStillDropped()
    {
        // A failed epic poll must make the epics wait a cycle, not hand them to the template that owns the tracker
        var epic = MakeIssue("epic-1", [AgentLabels.Epic]);
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));
        var executor = MakeTemplate("t1");
        var trackerOwner = MakeTemplate("t2") with { IssueProviderId = "ep-1" };
        var project = MakeProject("p1", "ep-1", [executor.Id, trackerOwner.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1", "t2");
        queues["t2"].Add(new EpicCandidate(epic, PipelineRunType.DecompositionAnalysis, "ep-1"));

        var lookup = new Dictionary<string, PipelineJobTemplate> { [executor.Id] = executor, [trackerOwner.Id] = trackerOwner };
        await poller.AddProjectEpicsAsync([project], lookup, 3, queues, CancellationToken.None);

        queues["t1"].Should().BeEmpty();
        queues["t2"].Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_TwoProjectsShareAnEpicTracker_TheFirstByNameOwnsIt()
    {
        var mockProvider = EpicProvider(MakeIssue("epic-1", [AgentLabels.Epic]));
        var templateA = MakeTemplate("tA");
        var templateB = MakeTemplate("tB");
        var projectB = new PipelineProject { Id = "p-b", Name = "Beta", Enabled = true, EpicIssueProviderId = "ep-1", TemplateIds = [templateB.Id] };
        var projectA = new PipelineProject { Id = "p-a", Name = "Alpha", Enabled = true, EpicIssueProviderId = "ep-1", TemplateIds = [templateA.Id] };
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("tA", "tB");

        var lookup = new Dictionary<string, PipelineJobTemplate> { [templateA.Id] = templateA, [templateB.Id] = templateB };
        await poller.AddProjectEpicsAsync([projectB, projectA], lookup, 3, queues, CancellationToken.None);

        queues["tA"].Should().ContainSingle();
        queues["tB"].Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_ProviderThrows_SwallowsExceptionAndKeepsQueues()
    {
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("network error"));

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = PolledQueues("t1");

        // Should not throw
        var act = () => poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, CancellationToken.None);

        await act.Should().NotThrowAsync();
        queues["t1"].Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectEpicsAsync_CancellationRequestedBeforeLoop_AddsNothing()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var template = MakeTemplate("t1");
        var project = MakeProject("p1", "ep-1", [template.Id]);
        var mockProvider = new Mock<IIssueProvider>(); // never called
        var poller = CreatePoller(issueProviders: new() { ["ep-1"] = mockProvider.Object });
        var queues = EmptyQueues();

        // Already cancelled — loop breaks immediately
        await poller.AddProjectEpicsAsync(
            [project], new Dictionary<string, PipelineJobTemplate> { [template.Id] = template }, 3, queues, cts.Token);

        queues.Should().BeEmpty("cancellation before loop causes immediate break");
        mockProvider.Verify(p => p.ListOpenIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<string[]>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddProjectEpicsAsync_MultipleProjects_ProcessesAll()
    {
        var mockProvider1 = EpicProvider(MakeIssue("e1", [AgentLabels.Epic]));
        var mockProvider2 = new Mock<IIssueProvider>();
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
        var queues = PolledQueues("t1", "t2");

        await poller.AddProjectEpicsAsync([project1, project2], lookup, 3, queues, CancellationToken.None);

        // p1 had an epic, p2 was empty so nothing was added to its executor
        queues.Should().ContainKey("t1");
        queues["t2"].Should().BeEmpty();
        queues["t1"].Should().ContainSingle();
    }
}
