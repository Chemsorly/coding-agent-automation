using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// The triage queues of <see cref="TemplatePoller"/>: a triage-enabled template polls its own tracker for
/// <c>agent:triage</c>, and a project's triage executor also takes the triage issues of the project's epic tracker.
/// </summary>
public class TemplatePollerTriageTests
{
    private static TemplatePoller CreatePoller(Dictionary<string, IIssueProvider> issueProviders)
    {
        var logger = Mock.Of<Serilog.ILogger>();
        var cacheManager = new ProviderCacheManager(new Mock<IProviderFactory>().Object, logger);
        foreach (var kvp in issueProviders)
            cacheManager.IssueProviders[kvp.Key] = kvp.Value;
        return new TemplatePoller(cacheManager, logger);
    }

    private static PipelineJobTemplate MakeTemplate(string id, bool triageEnabled = true, string issueProviderId = "ip-1") =>
        new()
        {
            Id = id,
            Name = $"Template-{id}",
            IssueProviderId = issueProviderId,
            RepoProviderId = "rp-1",
            Enabled = true,
            ImplementationEnabled = false,
            DecompositionEnabled = false,
            TriageEnabled = triageEnabled,
        };

    private static PipelineProject MakeProject(string id, string epicProviderId, params string[] templateIds) =>
        new() { Id = id, Name = $"Project-{id}", Enabled = true, EpicIssueProviderId = epicProviderId, TemplateIds = templateIds };

    private static IssueSummary MakeIssue(string id, string[]? labels = null, DateTime? createdAt = null) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = labels ?? [AgentLabels.Triage], CreatedAt = createdAt };

    /// <summary>A tracker whose <c>agent:triage</c> list returns <paramref name="issues"/> and every other list is empty.</summary>
    private static Mock<IIssueProvider> TriageTracker(params IssueSummary[] issues)
    {
        var provider = new Mock<IIssueProvider>();
        provider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 25, HasMore = false });
        provider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Triage)), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = issues, Page = 1, PageSize = 25, HasMore = false });
        return provider;
    }

    private static async Task<(Dictionary<string, List<IssueSummary>> IssueQueues, Dictionary<string, List<TriageCandidate>> TriageQueues)>
        Poll(TemplatePoller poller, params PipelineJobTemplate[] templates)
    {
        var (issueQueues, _, _, _, _, triageQueues) = await poller.PollTemplateQueuesAsync(
            templates, 3, new ConcurrentDictionary<string, ConfigStatusSnapshot>(), _ => { }, _ => { }, () => { },
            CancellationToken.None);
        return (issueQueues, triageQueues);
    }

    private static Dictionary<string, List<TriageCandidate>> PolledQueues(params string[] templateIds) =>
        templateIds.ToDictionary(id => id, _ => new List<TriageCandidate>());

    // ── The template's own tracker ──────────────────────────────────────────

    [Fact]
    public async Task PollTemplateQueues_TriageEnabled_QueuesTriageIssuesOfItsTrackerOldestFirst()
    {
        var older = MakeIssue("7", createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var newer = MakeIssue("9", createdAt: new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        var poller = CreatePoller(new() { ["ip-1"] = TriageTracker(newer, older).Object });

        var result = await Poll(poller, MakeTemplate("t1"));

        result.TriageQueues["t1"].Select(c => c.Issue.Identifier).Should().Equal("7", "9");
        result.TriageQueues["t1"].Should().OnlyContain(c => c.IssueProviderId == "ip-1");
    }

    [Fact]
    public async Task PollTemplateQueues_TriageDisabled_LeavesTheQueueEmptyAndNeverAsksForTriageIssues()
    {
        var tracker = TriageTracker(MakeIssue("7"));
        var poller = CreatePoller(new() { ["ip-1"] = tracker.Object });

        var result = await Poll(poller, MakeTemplate("t1", triageEnabled: false));

        result.TriageQueues["t1"].Should().BeEmpty();
        tracker.Verify(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
            It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Triage)), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(AgentLabels.InProgress)]
    [InlineData(AgentLabels.TriageReview)]
    [InlineData(AgentLabels.Error)]
    [InlineData(AgentLabels.Done)]
    public async Task PollTemplateQueues_IssueAlsoCarryingAStateLabel_IsNotQueued(string stateLabel)
    {
        var poller = CreatePoller(new() { ["ip-1"] = TriageTracker(MakeIssue("7", [AgentLabels.Triage, stateLabel])).Object });

        var result = await Poll(poller, MakeTemplate("t1"));

        result.TriageQueues["t1"].Should().BeEmpty();
    }

    [Fact]
    public async Task PollTemplateQueues_TriagePollFails_OtherQueuesAreKept()
    {
        var tracker = new Mock<IIssueProvider>();
        tracker
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = [MakeIssue("1", [AgentLabels.Next])], Page = 1, PageSize = 25, HasMore = false });
        tracker
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.Is<IReadOnlyList<string>?>(l => l != null && l.Contains(AgentLabels.Triage)), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("boom"));
        var poller = CreatePoller(new() { ["ip-1"] = tracker.Object });

        var result = await Poll(poller, MakeTemplate("t1") with { ImplementationEnabled = true });

        result.TriageQueues["t1"].Should().BeEmpty();
        result.IssueQueues["t1"].Should().ContainSingle();
    }

    // ── The project's epic tracker ──────────────────────────────────────────

    [Fact]
    public async Task AddProjectTriages_EpicTrackerIssues_JoinTheExecutorQueueBoundToTheEpicTracker()
    {
        var poller = CreatePoller(new() { ["ep-1"] = TriageTracker(MakeIssue("42")).Object });
        var executor = MakeTemplate("t1");
        var queues = PolledQueues("t1");

        await poller.AddProjectTriagesAsync(
            [MakeProject("p1", "ep-1", "t1")], new Dictionary<string, PipelineJobTemplate> { ["t1"] = executor }, 3, queues, CancellationToken.None);

        queues["t1"].Should().ContainSingle();
        queues["t1"][0].Issue.Identifier.Should().Be("42");
        queues["t1"][0].IssueProviderId.Should().Be("ep-1", "the run is bound to the tracker the issue lives in");
    }

    [Fact]
    public async Task AddProjectTriages_NoTemplateOfTheProjectTriages_DoesNotPollTheEpicTracker()
    {
        var tracker = TriageTracker(MakeIssue("42"));
        var poller = CreatePoller(new() { ["ep-1"] = tracker.Object });
        var queues = PolledQueues("t1");

        await poller.AddProjectTriagesAsync(
            [MakeProject("p1", "ep-1", "t1")],
            new Dictionary<string, PipelineJobTemplate> { ["t1"] = MakeTemplate("t1", triageEnabled: false) }, 3, queues, CancellationToken.None);

        queues["t1"].Should().BeEmpty();
        tracker.Verify(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task AddProjectTriages_EpicTrackerIsAlsoATemplatesTracker_EachIssueIsQueuedOnceForTheExecutor()
    {
        var issue = MakeIssue("42");
        var poller = CreatePoller(new() { ["ep-1"] = TriageTracker(issue).Object });
        var executor = MakeTemplate("t1");
        var trackerOwner = MakeTemplate("t2", issueProviderId: "ep-1");
        var queues = PolledQueues("t1", "t2");
        queues["t2"].Add(new TriageCandidate(issue, "ep-1")); // t2's own poll of the same tracker

        await poller.AddProjectTriagesAsync(
            [MakeProject("p1", "ep-1", "t1", "t2")],
            new Dictionary<string, PipelineJobTemplate> { ["t1"] = executor, ["t2"] = trackerOwner }, 3, queues, CancellationToken.None);

        queues["t2"].Should().BeEmpty();
        queues["t1"].Should().ContainSingle().Which.IssueProviderId.Should().Be("ep-1");
    }

    [Fact]
    public async Task AddProjectTriages_ExecutorNotPolled_TriagesWaitAndTheTrackerOwnersCopiesAreDropped()
    {
        var issue = MakeIssue("42");
        var poller = CreatePoller(new() { ["ep-1"] = TriageTracker(issue).Object });
        var queues = PolledQueues("t2");
        queues["t2"].Add(new TriageCandidate(issue, "ep-1"));

        await poller.AddProjectTriagesAsync(
            [MakeProject("p1", "ep-1", "t1", "t2")],
            new Dictionary<string, PipelineJobTemplate> { ["t1"] = MakeTemplate("t1"), ["t2"] = MakeTemplate("t2", issueProviderId: "ep-1") },
            3, queues, CancellationToken.None);

        queues.Should().NotContainKey("t1");
        queues["t2"].Should().BeEmpty();
    }

    [Fact]
    public async Task AddProjectTriages_PollFails_SwallowsTheErrorAndKeepsTheQueues()
    {
        var tracker = new Mock<IIssueProvider>();
        tracker
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("rate limited"));
        var poller = CreatePoller(new() { ["ep-1"] = tracker.Object });
        var queues = PolledQueues("t1");
        queues["t1"].Add(new TriageCandidate(MakeIssue("1"), "ip-1"));

        await poller.AddProjectTriagesAsync(
            [MakeProject("p1", "ep-1", "t1")], new Dictionary<string, PipelineJobTemplate> { ["t1"] = MakeTemplate("t1") }, 3, queues, CancellationToken.None);

        queues["t1"].Should().ContainSingle().Which.IssueProviderId.Should().Be("ip-1");
    }

    [Fact]
    public void SelectTriageTemplate_PicksTheFirstEnabledTemplateWithTriageInProjectOrder()
    {
        var lookup = new Dictionary<string, PipelineJobTemplate>
        {
            ["a"] = MakeTemplate("a", triageEnabled: false),
            ["b"] = MakeTemplate("b") with { Enabled = false },
            ["c"] = MakeTemplate("c"),
            ["d"] = MakeTemplate("d"),
        };

        TemplatePoller.SelectTriageTemplate(MakeProject("p1", "ep-1", "a", "b", "c", "d"), lookup)!.Id.Should().Be("c");
        TemplatePoller.SelectTriageTemplate(MakeProject("p1", "ep-1", "a", "b"), lookup).Should().BeNull();
    }
}
