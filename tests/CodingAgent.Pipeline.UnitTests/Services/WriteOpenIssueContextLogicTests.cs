using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Behavioral tests for the open-issue context writing logic now embedded in
/// <see cref="WriteOpenIssueContextStep"/> as private static helpers.
/// Tests are exercised via <c>ExecuteAsync</c> to avoid assembly coupling
/// (the helpers are private, not internal).
/// Feature: 027-epic-decomposition-pipeline, Requirements: 8.1, 8.2, 8.5, 8.7
/// </summary>
public class WriteOpenIssueContextLogicTests : IDisposable
{
    private static readonly string[] s_AgentDoneLabel = new[] { "agent:done" };

    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly string _workspacePath;

    public WriteOpenIssueContextLogicTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"open-issue-logic-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineStepContext BuildContext(
        PipelineRunType runType = PipelineRunType.Implementation,
        int maxIssues = 50,
        Serilog.ILogger? logger = null) =>
        new()
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Test",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = runType,
                WorkspacePath = _workspacePath
            },
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                MaxOpenIssuesForContext = maxIssues
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(logger ?? _logger),
            Logger = logger ?? _logger
        };

    private static PagedResult<IssueSummary> Page(bool hasMore = false, params IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 30, HasMore = hasMore };

    private static IssueSummary Summary(string id, string[]? labels = null) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = labels ?? Array.Empty<string>() };

    private static IssueDetail Detail(string id, string? title = null, string? description = null, string[]? labels = null) =>
        new()
        {
            Identifier = id,
            Title = title ?? $"Issue {id}",
            Description = description ?? $"Body of {id}",
            Labels = labels ?? Array.Empty<string>()
        };

    private async Task<int> RunStep(PipelineRunType runType = PipelineRunType.Implementation, int maxIssues = 50, Serilog.ILogger? logger = null)
    {
        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(runType, maxIssues, logger);
        await step.ExecuteAsync(context, CancellationToken.None);
        return context.Run.OpenIssuesDownloaded;
    }

    // ── Directory creation ───────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_CreatesDirectory()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());

        await RunStep();

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.Exists(outputDir).Should().BeTrue();
    }

    // ── File format ──────────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_WritesCorrectFileFormat()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, new IssueSummary
            {
                Identifier = "42",
                Title = "Add pagination",
                Labels = new[] { "agent:next", "enhancement" }
            }));

        _issueOps.Setup(x => x.GetIssueAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "42",
                Title = "Add pagination",
                Description = "Implement pagination for the /users endpoint.",
                Labels = new[] { "agent:next", "enhancement" }
            });

        var count = await RunStep();

        count.Should().Be(1);

        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "42.md");
        File.Exists(filePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("---");
        content.Should().Contain("identifier: \"42\"");
        content.Should().Contain("title: \"Add pagination\"");
        content.Should().Contain("labels: [\"agent:next\", \"enhancement\"]");
        content.Should().Contain("Implement pagination for the /users endpoint.");
    }

    // ── Max issues cap ───────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_EnforcesMaxIssuesCap()
    {
        var issues = Enumerable.Range(1, 5).Select(i => Summary(i.ToString())).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, issues));

        foreach (var issue in issues)
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(issue.Identifier));
        }

        var count = await RunStep(maxIssues: 3);

        count.Should().Be(3);
    }

    // ── Fault tolerance ──────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_IndividualFetchFailure_ContinuesWithOthers()
    {
        var issues = new[]
        {
            Summary("1"), Summary("2"), Summary("3")
        };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, issues));

        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));
        _issueOps.Setup(x => x.GetIssueAsync("3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("3"));

        var count = await RunStep();

        count.Should().Be(2, "issue 2 failed but 1 and 3 should succeed");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_ListingFails_ReturnsZero()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var count = await RunStep();

        count.Should().Be(0);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_NoOpenIssues_ReturnsZero()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());

        var count = await RunStep();

        count.Should().Be(0);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_MaxIssuesLessThanOne_ClampsToOne()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        var count = await RunStep(maxIssues: 0);

        count.Should().Be(1, "maxIssues < 1 must clamp to 1");
    }

    // ── Closed siblings ──────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_IncludesClosedSiblings()
    {
        var openIssues = new[] { Summary("10"), Summary("11") };
        var closedIssues = new[] { Summary("5", s_AgentDoneLabel), Summary("6", s_AgentDoneLabel) };

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, openIssues));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, closedIssues));

        foreach (var issue in openIssues.Concat(closedIssues))
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(issue.Identifier, labels: issue.Labels.ToArray()));
        }

        var count = await RunStep(PipelineRunType.DecompositionAnalysis);

        count.Should().Be(4, "2 open + 2 closed");

        var closedFile = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "5.md"));
        closedFile.Should().Contain("status: closed");

        var openFile = await File.ReadAllTextAsync(
            Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "10.md"));
        openFile.Should().NotContain("status: closed");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_NonEpicRun_DoesNotFetchClosedIssues()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        await RunStep(PipelineRunType.Implementation);

        _issueOps.Verify(x => x.ListClosedIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_RespectsTotalBudget()
    {
        // Max = 8 → openBudget = 6, closedBudget = 2
        var openIssues = Enumerable.Range(1, 10).Select(i => Summary($"open-{i}")).ToArray();
        var closedIssues = Enumerable.Range(1, 5).Select(i => Summary($"closed-{i}")).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, openIssues));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, closedIssues));

        foreach (var issue in openIssues.Concat(closedIssues))
        {
            _issueOps.Setup(x => x.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(issue.Identifier));
        }

        var count = await RunStep(PipelineRunType.DecompositionAnalysis, maxIssues: 8);

        // TODO: This assertion only checks the total count (8), not the individual open/closed split.
        // A regression where closedBudget=0 and openBudget=8 would still yield count==8 (from 8 open
        // issues), causing this test to pass incorrectly. Consider independently verifying that exactly
        // 6 open and 2 closed files were written (e.g. check that "open-7.md" is absent and "closed-2.md"
        // is absent, while "open-6.md" and "closed-2.md" are present).
        count.Should().Be(8, "6 open + 2 closed = 8 total (respects budget)");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_ClosedListingFails_StillWritesOpenIssues()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("1")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        var count = await RunStep(PipelineRunType.DecompositionAnalysis);

        count.Should().Be(1, "closed listing failed but open issue must still be written");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_DeduplicatesClosedFromOpen()
    {
        // Issue "1" appears in both lists — must only be written once
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("1")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("1"), Summary("2")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("2"));

        var count = await RunStep(PipelineRunType.DecompositionAnalysis);

        // TODO: This test only asserts total count == 2, not which version of "1" was written.
        // A regression where the closed version overwrites the open version (writing "1.md" with
        // "status: closed") would not be caught — the count remains 2. Consider also asserting
        // that the file for "1" does NOT contain "status: closed", confirming the open version
        // was preserved rather than overwritten by the closed one.
        count.Should().Be(2, "1 open + 1 closed (deduplicated '1' removed from closed)");
    }

    // ── Budget edge cases ────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesOne_AllocatesSlotToOpen()
    {
        // maxIssues=1 with includeClosedSiblings: open must get at least 1 slot
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("open-1")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("closed-1")));
        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("open-1"));
        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("closed-1", labels: s_AgentDoneLabel));

        var count = await RunStep(PipelineRunType.DecompositionAnalysis, maxIssues: 1);

        count.Should().Be(1, "closedBudget=0 when maxIssues=1, so only the open slot is used");

        var openFile = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-1.md");
        File.Exists(openFile).Should().BeTrue();
        (await File.ReadAllTextAsync(openFile)).Should().NotContain("status: closed");

        var closedFile = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md");
        File.Exists(closedFile).Should().BeFalse("closed-1 must not be written when budget is 0");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesTwo_BothOpenAndClosed()
    {
        // maxIssues=2 → openBudget=1, closedBudget=1
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("open-1"), Summary("open-2")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, Summary("closed-1"), Summary("closed-2")));
        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("open-1"));
        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("closed-1"));

        var count = await RunStep(PipelineRunType.DecompositionAnalysis, maxIssues: 2);

        count.Should().Be(2, "1 open + 1 closed");

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        File.Exists(Path.Combine(outputDir, "open-1.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "closed-1.md")).Should().BeTrue();

        var closedContent = await File.ReadAllTextAsync(Path.Combine(outputDir, "closed-1.md"));
        closedContent.Should().Contain("status: closed");
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_EpicRun_MaxIssuesThree_OpenGetsPriority()
    {
        // maxIssues=3 → openBudget=2, closedBudget=1
        var openIssues = Enumerable.Range(1, 3).Select(i => Summary($"open-{i}")).ToArray();
        var closedIssues = Enumerable.Range(1, 3).Select(i => Summary($"closed-{i}")).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, openIssues));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, closedIssues));

        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>())).ReturnsAsync(Detail("open-1"));
        _issueOps.Setup(x => x.GetIssueAsync("open-2", It.IsAny<CancellationToken>())).ReturnsAsync(Detail("open-2"));
        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>())).ReturnsAsync(Detail("closed-1"));

        var count = await RunStep(PipelineRunType.DecompositionAnalysis, maxIssues: 3);

        count.Should().Be(3, "2 open + 1 closed");

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        File.Exists(Path.Combine(outputDir, "open-1.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "open-2.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "closed-1.md")).Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "open-3.md")).Should().BeFalse("over budget");

        var closedContent = await File.ReadAllTextAsync(Path.Combine(outputDir, "closed-1.md"));
        closedContent.Should().Contain("status: closed");
    }

    // ── Pagination ───────────────────────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_Pagination_FetchesMultiplePages()
    {
        var page1Issues = Enumerable.Range(1, 30).Select(i => Summary(i.ToString())).ToArray();
        var page2Issues = Enumerable.Range(31, 5).Select(i => Summary(i.ToString())).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = page1Issues, Page = 1, PageSize = 30, HasMore = true });
        _issueOps.Setup(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = page2Issues, Page = 2, PageSize = 30, HasMore = false });

        for (var i = 1; i <= 35; i++)
        {
            var id = i.ToString();
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }

        var count = await RunStep();

        count.Should().Be(35);
        _issueOps.Verify(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── AC2: Degraded-context warning ────────────────────────────────────

    [Fact]
    public async Task WriteOpenIssueContextAsync_AllFetchesFail_EmitsWarningWhenIdentifiersCollected()
    {
        var mockLogger = new Mock<Serilog.ILogger>();

        var issues = new[] { Summary("1"), Summary("2"), Summary("3") };
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page(false, issues));
        _issueOps.Setup(x => x.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("HubException: Failed to invoke 'RequestGetIssue'"));

        var count = await RunStep(logger: mockLogger.Object);

        count.Should().Be(0);

        // TODO: This assertion targets the raw Serilog message template text ("0 files" and
        // "{TotalIdentifiers}"), not the rendered output. If the template wording changes,
        // the assertion silently stops matching even though the behaviour is correct. Consider
        // asserting on the observable outcome (count == 0, warning was called at all with the
        // right numeric arguments) rather than coupling to the template string literal.
        mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("0 files") && s.Contains("{TotalIdentifiers}")),
                It.Is<int>(n => n == 3),  // TotalIdentifiers
                It.Is<int>(n => n == 3),  // OpenCount
                It.Is<int>(n => n == 0)), // ClosedCount
            Times.Once);
    }

    [Fact]
    public async Task WriteOpenIssueContextAsync_NoIdentifiersCollected_DoesNotEmitDegradedWarning()
    {
        var mockLogger = new Mock<Serilog.ILogger>();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Page());

        await RunStep(logger: mockLogger.Object);

        mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("all RequestGetIssue calls may have failed")),
                It.IsAny<int>(),
                It.IsAny<int>(),
                It.IsAny<int>()),
            Times.Never);
    }

    // ── FormatIssueMarkdown ──────────────────────────────────────────────

    [Fact]
    public void FormatIssueMarkdown_ProducesCorrectYamlFrontMatter()
    {
        var detail = new IssueDetail
        {
            Identifier = "99",
            Title = "Test issue with \"quotes\"",
            Description = "The body content",
            Labels = new[] { "bug", "priority:high" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail);

        markdown.Should().Contain("---");
        markdown.Should().Contain("identifier: \"99\"");
        markdown.Should().Contain("title: \"Test issue with \\\"quotes\\\"\"");
        markdown.Should().Contain("labels: [\"bug\", \"priority:high\"]");
        markdown.Should().Contain("The body content");
    }

    [Fact]
    public void FormatIssueMarkdown_EmptyLabels_ProducesEmptyArray()
    {
        var detail = new IssueDetail
        {
            Identifier = "1",
            Title = "Simple",
            Description = "Body",
            Labels = Array.Empty<string>()
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail);

        markdown.Should().Contain("labels: []");
    }

    [Fact]
    public void FormatIssueMarkdown_ClosedIssue_IncludesStatusField()
    {
        var detail = new IssueDetail
        {
            Identifier = "99",
            Title = "Completed task",
            Description = "This was done",
            Labels = new[] { "agent:done" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail, isClosed: true);

        markdown.Should().Contain("status: closed");
        markdown.Should().Contain("identifier: \"99\"");
        markdown.Should().Contain("title: \"Completed task\"");
    }

    [Fact]
    public void FormatIssueMarkdown_OpenIssue_DoesNotIncludeStatusField()
    {
        var detail = new IssueDetail
        {
            Identifier = "100",
            Title = "Open task",
            Description = "This is in progress",
            Labels = new[] { "agent:next" }
        };

        var markdown = OpenIssueContextWriter.FormatIssueMarkdown(detail, isClosed: false);

        markdown.Should().NotContain("status:");
    }
}
