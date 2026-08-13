using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the <see cref="TemplatePoller"/> internal static methods introduced in this PR:
/// <see cref="TemplatePoller.IsAuthError"/>,
/// <see cref="TemplatePoller.ClearQueuesForTemplate"/>,
/// <see cref="TemplatePoller.FetchAllPagesAsync{T}"/>, and
/// <see cref="TemplatePoller.SelectDecompositionTemplate"/>.
/// </summary>
public class TemplatePolllerStaticMethodTests
{
    // ── IsAuthError ──────────────────────────────────────────────────────

    [Fact]
    public void IsAuthError_Http401_ReturnsTrue()
    {
        var ex = new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
        TemplatePoller.IsAuthError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthError_Http403_ReturnsTrue()
    {
        var ex = new HttpRequestException("Forbidden", null, System.Net.HttpStatusCode.Forbidden);
        TemplatePoller.IsAuthError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthError_Http404_ReturnsFalse()
    {
        var ex = new HttpRequestException("Not Found", null, System.Net.HttpStatusCode.NotFound);
        TemplatePoller.IsAuthError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthError_Http500_ReturnsFalse()
    {
        var ex = new HttpRequestException("Server Error", null, System.Net.HttpStatusCode.InternalServerError);
        TemplatePoller.IsAuthError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthError_MessageContainsUnauthorized_ReturnsTrue()
    {
        var ex = new InvalidOperationException("unauthorized access");
        TemplatePoller.IsAuthError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthError_MessageContainsForbidden_ReturnsTrue()
    {
        var ex = new InvalidOperationException("forbidden operation");
        TemplatePoller.IsAuthError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthError_MessageContainsCredential_ReturnsTrue()
    {
        var ex = new InvalidOperationException("invalid credential provided");
        TemplatePoller.IsAuthError(ex).Should().BeTrue();
    }

    [Fact]
    public void IsAuthError_GenericException_ReturnsFalse()
    {
        var ex = new InvalidOperationException("something went wrong");
        TemplatePoller.IsAuthError(ex).Should().BeFalse();
    }

    [Fact]
    public void IsAuthError_NullStatusCodeHttpException_ReturnsFalse()
    {
        var ex = new HttpRequestException("network error");
        TemplatePoller.IsAuthError(ex).Should().BeFalse();
    }

    // ── ClearQueuesForTemplate ────────────────────────────────────────────

    [Fact]
    public void ClearQueuesForTemplate_SetsEmptyListsForTemplate()
    {
        var templateId = new TemplateId("tpl-1");
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            [templateId.Value] = [MakeIssue("i1")]
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            [templateId.Value] = [MakePr(1)]
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            [templateId.Value] = [(MakeIssue("epic1"), PipelineRunType.DecompositionAnalysis)]
        };

        var agentDonePrQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            [templateId.Value] = [MakePr(99)]
        };

        TemplatePoller.ClearQueuesForTemplate(templateId, issueQueues, prQueues, decompQueues, agentDonePrQueues);

        issueQueues[templateId.Value].Should().BeEmpty();
        prQueues[templateId.Value].Should().BeEmpty();
        decompQueues[templateId.Value].Should().BeEmpty();
        agentDonePrQueues[templateId.Value].Should().BeEmpty("ClearQueuesForTemplate must also clear agentDonePrQueues");
    }

    [Fact]
    public void ClearQueuesForTemplate_WhenKeyNotPresent_AddsEmptyLists()
    {
        var templateId = new TemplateId("tpl-new");
        var issueQueues = new Dictionary<string, List<IssueSummary>>();
        var prQueues = new Dictionary<string, List<PullRequestSummary>>();
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>();

        var agentDonePrQueues = new Dictionary<string, List<PullRequestSummary>>();

        TemplatePoller.ClearQueuesForTemplate(templateId, issueQueues, prQueues, decompQueues, agentDonePrQueues);

        issueQueues.Should().ContainKey(templateId.Value);
        prQueues.Should().ContainKey(templateId.Value);
        decompQueues.Should().ContainKey(templateId.Value);
        agentDonePrQueues.Should().ContainKey(templateId.Value, "ClearQueuesForTemplate must initialise agentDonePrQueues entry");
        issueQueues[templateId.Value].Should().BeEmpty();
        prQueues[templateId.Value].Should().BeEmpty();
        decompQueues[templateId.Value].Should().BeEmpty();
        agentDonePrQueues[templateId.Value].Should().BeEmpty();
    }

    [Fact]
    public void ClearQueuesForTemplate_OtherTemplates_Unaffected()
    {
        var templateId = new TemplateId("tpl-1");
        const string otherId = "tpl-other";
        var issueQueues = new Dictionary<string, List<IssueSummary>>
        {
            [templateId.Value] = [MakeIssue("i1")],
            [otherId] = [MakeIssue("i2")]
        };
        var prQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            [templateId.Value] = [],
            [otherId] = [MakePr(1)]
        };
        var decompQueues = new Dictionary<string, List<(IssueSummary Issue, PipelineRunType Phase)>>
        {
            [templateId.Value] = [],
            [otherId] = []
        };

        var agentDonePrQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            [templateId.Value] = [MakePr(42)],
            [otherId] = [MakePr(43)]
        };

        TemplatePoller.ClearQueuesForTemplate(templateId, issueQueues, prQueues, decompQueues, agentDonePrQueues);

        issueQueues[otherId].Should().HaveCount(1, "other template's queue must be untouched");
        prQueues[otherId].Should().HaveCount(1, "other template's PR queue must be untouched");
        agentDonePrQueues[otherId].Should().HaveCount(1, "other template's agentDone queue must be untouched");
        agentDonePrQueues[templateId.Value].Should().BeEmpty("only the target template's agentDone queue is cleared");
    }

    // ── FetchAllPagesAsync ────────────────────────────────────────────────

    [Fact]
    public async Task FetchAllPagesAsync_SinglePage_ReturnsAllItems()
    {
        var result = await TemplatePoller.FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, ct) => Task.FromResult(MakePagedResult([MakeIssue("i1"), MakeIssue("i2")], hasMore: false)),
            maxPages: 3,
            ct: CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Identifier.Should().Be("i1");
        result[1].Identifier.Should().Be("i2");
    }

    [Fact]
    public async Task FetchAllPagesAsync_MultiplePages_AggregatesAll()
    {
        var callCount = 0;

        var result = await TemplatePoller.FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, ct) =>
            {
                callCount++;
                return page switch
                {
                    1 => Task.FromResult(MakePagedResult([MakeIssue("i1")], hasMore: true, currentPage: 1)),
                    2 => Task.FromResult(MakePagedResult([MakeIssue("i2")], hasMore: true, currentPage: 2)),
                    _ => Task.FromResult(MakePagedResult([MakeIssue("i3")], hasMore: false, currentPage: page))
                };
            },
            maxPages: 5,
            ct: CancellationToken.None);

        result.Should().HaveCount(3);
        callCount.Should().Be(3);
    }

    [Fact]
    public async Task FetchAllPagesAsync_StopsAtMaxPages()
    {
        var callCount = 0;

        var result = await TemplatePoller.FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, ct) =>
            {
                callCount++;
                return Task.FromResult(MakePagedResult([MakeIssue($"i{page}")], hasMore: true, currentPage: page));
            },
            maxPages: 2,
            ct: CancellationToken.None);

        callCount.Should().Be(2, "should stop at maxPages even when HasMore=true");
        result.Should().HaveCount(2);
    }

    [Fact]
    public async Task FetchAllPagesAsync_EmptyFirstPage_ReturnsEmpty()
    {
        var result = await TemplatePoller.FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, ct) => Task.FromResult(MakePagedResult(Array.Empty<IssueSummary>(), hasMore: false)),
            maxPages: 3,
            ct: CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FetchAllPagesAsync_CancellationRequested_ThrowsOperationCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => TemplatePoller.FetchAllPagesAsync<IssueSummary>(
            (page, pageSize, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(MakePagedResult(Array.Empty<IssueSummary>(), hasMore: false));
            },
            maxPages: 3,
            ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── SelectDecompositionTemplate ───────────────────────────────────────

    [Fact]
    public void SelectDecompositionTemplate_NoTemplatesInProject_ReturnsNull()
    {
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = [] };
        var result = TemplatePoller.SelectDecompositionTemplate(project, new Dictionary<string, PipelineJobTemplate>());
        result.Should().BeNull();
    }

    [Fact]
    public void SelectDecompositionTemplate_NoDecompositionEnabledTemplate_ReturnsNull()
    {
        var tpl = MakeTemplate("t1", enabled: true, decompositionEnabled: false);
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = [tpl.Id] };

        var result = TemplatePoller.SelectDecompositionTemplate(project, new Dictionary<string, PipelineJobTemplate> { [tpl.Id] = tpl });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectDecompositionTemplate_DisabledTemplate_ReturnsNull()
    {
        var tpl = MakeTemplate("t1", enabled: false, decompositionEnabled: true);
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = [tpl.Id] };

        var result = TemplatePoller.SelectDecompositionTemplate(project, new Dictionary<string, PipelineJobTemplate> { [tpl.Id] = tpl });

        result.Should().BeNull();
    }

    [Fact]
    public void SelectDecompositionTemplate_EnabledDecompositionTemplate_ReturnsIt()
    {
        var tpl = MakeTemplate("t1", enabled: true, decompositionEnabled: true);
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = [tpl.Id] };

        var result = TemplatePoller.SelectDecompositionTemplate(project, new Dictionary<string, PipelineJobTemplate> { [tpl.Id] = tpl });

        result.Should().BeSameAs(tpl);
    }

    [Fact]
    public void SelectDecompositionTemplate_FirstDisabledThenEnabled_ReturnsSecond()
    {
        var tpl1 = MakeTemplate("t1", enabled: false, decompositionEnabled: true);
        var tpl2 = MakeTemplate("t2", enabled: true, decompositionEnabled: true);
        var tpl3 = MakeTemplate("t3", enabled: true, decompositionEnabled: true);
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = [tpl1.Id, tpl2.Id, tpl3.Id] };
        var lookup = new Dictionary<string, PipelineJobTemplate>
        {
            [tpl1.Id] = tpl1, [tpl2.Id] = tpl2, [tpl3.Id] = tpl3
        };

        var result = TemplatePoller.SelectDecompositionTemplate(project, lookup);

        result.Should().BeSameAs(tpl2, "first enabled+decomposition template is t2");
    }

    [Fact]
    public void SelectDecompositionTemplate_TemplateNotInLookup_ReturnsNull()
    {
        var project = new PipelineProject { Id = "p1", Name = "P1", TemplateIds = ["missing-id"] };

        var result = TemplatePoller.SelectDecompositionTemplate(project, new Dictionary<string, PipelineJobTemplate>());

        result.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static IssueSummary MakeIssue(string id) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = [] };

    private static PullRequestSummary MakePr(int number) =>
        new()
        {
            Number = number,
            Identifier = number.ToString(),
            Title = $"PR {number}",
            Description = "",
            Labels = [],
            BranchName = $"feature/pr-{number}",
            TargetBranch = "main",
            Url = $"https://github.com/owner/repo/pull/{number}",
            IsDraft = false
        };

    private static PagedResult<T> MakePagedResult<T>(IEnumerable<T> items, bool hasMore, int currentPage = 1) =>
        new()
        {
            Items = items.ToList(),
            Page = currentPage,
            PageSize = 25,
            HasMore = hasMore
        };

    private static PipelineJobTemplate MakeTemplate(string id, bool enabled, bool decompositionEnabled) =>
        new()
        {
            Id = id,
            Name = $"Template-{id}",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            Enabled = enabled,
            DecompositionEnabled = decompositionEnabled
        };
}
