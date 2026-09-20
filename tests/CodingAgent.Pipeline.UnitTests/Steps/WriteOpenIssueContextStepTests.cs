using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="WriteOpenIssueContextStep"/>.
/// Covers success paths (implementation + decomposition), transition call,
/// count propagation, file format, and graceful degradation.
/// </summary>
public class WriteOpenIssueContextStepTests : IDisposable
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly string _workspacePath;

    public WriteOpenIssueContextStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"write-open-issue-step-test-{Guid.NewGuid():N}");
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
        int maxOpenIssuesForContext = 25,
        ILogger? logger = null) =>
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
                MaxOpenIssuesForContext = maxOpenIssuesForContext
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
            PrOrchestrator = new PullRequestOrchestrator(logger ?? Logger),
            Logger = logger ?? Logger
        };

    private static PagedResult<IssueSummary> SinglePageResult(params IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 30, HasMore = false };

    private static IssueSummary Summary(string id) =>
        new() { Identifier = id, Title = $"Issue {id}", Labels = Array.Empty<string>() };

    private static IssueDetail Detail(string id, string? title = null, string? description = null, string[]? labels = null) =>
        new()
        {
            Identifier = id,
            Title = title ?? $"Issue {id}",
            Description = description ?? $"Body of {id}",
            Labels = labels ?? Array.Empty<string>()
        };

    // ── ExecuteAsync — basic routing ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ImplementationRun_ReturnsContinue_AndStoresCount()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("10")));
        _issueOps.Setup(x => x.GetIssueAsync("10", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("10"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Implementation);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.OpenIssuesDownloaded.Should().Be(1);
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.DownloadingOpenIssues), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionAnalysisRun_IncludesClosedSiblings()
    {
        // Non-epic run does NOT call ListClosedIssuesAsync — verify it IS called for DecompositionAnalysis
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult());

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.DecompositionAnalysis);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.ListClosedIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once,
            "DecompositionAnalysis is epic-scoped and must fetch closed siblings");
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionRun_IncludesClosedSiblings()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult());

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Decomposition);

        await step.ExecuteAsync(context, CancellationToken.None);

        _issueOps.Verify(x => x.ListClosedIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ImplementationRun_DoesNotFetchClosedIssues()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Implementation);

        await step.ExecuteAsync(context, CancellationToken.None);

        _issueOps.Verify(x => x.ListClosedIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRun_DoesNotFetchClosedIssues()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Review);

        await step.ExecuteAsync(context, CancellationToken.None);

        _issueOps.Verify(x => x.ListClosedIssuesAsync(
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
            It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_PassesMaxIssuesFromConfig()
    {
        // With MaxOpenIssuesForContext = 3, only 3 issues should be fetched
        var issues = Enumerable.Range(1, 5)
            .Select(i => Summary(i.ToString()))
            .ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(issues));
        for (var i = 1; i <= 5; i++)
        {
            var id = i.ToString();
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(maxOpenIssuesForContext: 3);

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(3,
            "MaxOpenIssuesForContext = 3 must cap the number of files written");
    }

    // ── ExecuteAsync — file system ───────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_CreatesOpenIssuesDirectory()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult());

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.Exists(outputDir).Should().BeTrue("the step must create the output directory");
    }

    [Fact]
    public async Task ExecuteAsync_WritesCorrectFileFormat()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(new IssueSummary
            {
                Identifier = "42",
                Title = "Add pagination",
                Labels = new[] { "agent:next", "enhancement" }
            }));
        _issueOps.Setup(x => x.GetIssueAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("42", "Add pagination", "Implement pagination for the /users endpoint.",
                new[] { "agent:next", "enhancement" }));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext();

        var count = await step.ExecuteAsync(context, CancellationToken.None);

        count.Should().Be(StepResult.Continue);
        context.Run.OpenIssuesDownloaded.Should().Be(1);

        var filePath = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "42.md");
        File.Exists(filePath).Should().BeTrue();

        var content = await File.ReadAllTextAsync(filePath);
        content.Should().Contain("---");
        content.Should().Contain("identifier: \"42\"");
        content.Should().Contain("title: \"Add pagination\"");
        content.Should().Contain("labels: [\"agent:next\", \"enhancement\"]");
        content.Should().Contain("Implement pagination for the /users endpoint.");
    }

    [Fact]
    public async Task ExecuteAsync_EnforcesMaxIssuesCap()
    {
        var issues = Enumerable.Range(1, 5).Select(i => Summary(i.ToString())).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(issues));
        for (var i = 1; i <= 5; i++)
        {
            var id = i.ToString();
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(maxOpenIssuesForContext: 3);

        await step.ExecuteAsync(context, CancellationToken.None);

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.GetFiles(outputDir).Should().HaveCount(3);
    }

    [Fact]
    public async Task ExecuteAsync_IndividualFetchFailure_ContinuesWithOthers()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1"), Summary("2"), Summary("3")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));
        _issueOps.Setup(x => x.GetIssueAsync("3", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("3"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(2, "issue 2 failed but 1 and 3 should succeed");
    }

    [Fact]
    public async Task ExecuteAsync_ListingFails_ReturnsZeroAndStepContinues()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unavailable"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.OpenIssuesDownloaded.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_NoOpenIssues_ReturnsZero()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult());

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext();

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_MaxIssuesLessThanOne_ClampsToOne()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(maxOpenIssuesForContext: 0); // should clamp to 1

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionRun_IncludesClosedSiblingsWithBudgetSplit()
    {
        // Max = 8 → openBudget = 6, closedBudget = 2
        var openIssues = Enumerable.Range(1, 10).Select(i => Summary($"open-{i}")).ToArray();
        var closedIssues = Enumerable.Range(1, 5).Select(i => Summary($"closed-{i}")).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(openIssues));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(closedIssues));

        for (var i = 1; i <= 10; i++)
        {
            var id = $"open-{i}";
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }
        for (var i = 1; i <= 5; i++)
        {
            var id = $"closed-{i}";
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Decomposition, maxOpenIssuesForContext: 8);

        await step.ExecuteAsync(context, CancellationToken.None);

        // TODO: This only checks the total count (8), not the open/closed split. A regression where
        // closedBudget=0 and openBudget=8 would still yield OpenIssuesDownloaded==8 (from 8 open
        // issues), making this test pass incorrectly. Consider also asserting that e.g. "open-7.md"
        // is absent (over open budget) and "closed-1.md" / "closed-2.md" are present (within closed
        // budget) to independently verify the budget-split arithmetic.
        context.Run.OpenIssuesDownloaded.Should().Be(8, "6 open + 2 closed = 8 total");
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionRun_ClosedIssueHasStatusClosedInFrontMatter()
    {
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("open-1")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("closed-1")));
        _issueOps.Setup(x => x.GetIssueAsync("open-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("open-1"));
        _issueOps.Setup(x => x.GetIssueAsync("closed-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("closed-1", labels: new[] { "agent:done" }));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Decomposition, maxOpenIssuesForContext: 50);

        await step.ExecuteAsync(context, CancellationToken.None);

        var closedFilePath = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "closed-1.md");
        var openFilePath = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory, "open-1.md");

        (await File.ReadAllTextAsync(closedFilePath)).Should().Contain("status: closed");
        (await File.ReadAllTextAsync(openFilePath)).Should().NotContain("status: closed");
    }

    [Fact]
    public async Task ExecuteAsync_DeduplicatesClosedAndOpenIdentifiers()
    {
        // "1" appears in both open and closed — should only be written once (as open)
        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1")));
        _issueOps.Setup(x => x.ListClosedIssuesAsync(1, 30, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(SinglePageResult(Summary("1"), Summary("2")));
        _issueOps.Setup(x => x.GetIssueAsync("1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("1"));
        _issueOps.Setup(x => x.GetIssueAsync("2", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Detail("2"));

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(PipelineRunType.Decomposition, maxOpenIssuesForContext: 50);

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(2, "1 open + 1 deduplicated closed = 2 total");

        var outputDir = Path.Combine(_workspacePath, AgentWorkspacePaths.OpenIssuesDirectory);
        Directory.GetFiles(outputDir).Should().HaveCount(2);
        // TODO: This test only asserts total file count and total count, not which version of "1"
        // was written. A regression where the closed version overwrites the open version (writing
        // "1.md" with "status: closed") would not be caught. Consider also asserting that the
        // file for "1" does NOT contain "status: closed", confirming the open version was preserved.
    }

    [Fact]
    public async Task ExecuteAsync_PaginatorFetchesMultiplePages()
    {
        var page1 = Enumerable.Range(1, 30).Select(i => Summary(i.ToString())).ToArray();
        var page2 = Enumerable.Range(31, 5).Select(i => Summary(i.ToString())).ToArray();

        _issueOps.Setup(x => x.ListOpenIssuesAsync(1, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = page1, Page = 1, PageSize = 30, HasMore = true });
        _issueOps.Setup(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = page2, Page = 2, PageSize = 30, HasMore = false });

        for (var i = 1; i <= 35; i++)
        {
            var id = i.ToString();
            _issueOps.Setup(x => x.GetIssueAsync(id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(Detail(id));
        }

        var step = new WriteOpenIssueContextStep();
        var context = BuildContext(maxOpenIssuesForContext: 50);

        await step.ExecuteAsync(context, CancellationToken.None);

        context.Run.OpenIssuesDownloaded.Should().Be(35);
        _issueOps.Verify(x => x.ListOpenIssuesAsync(2, 30, null, It.IsAny<CancellationToken>()), Times.Once,
            "second page must be fetched when HasMore = true");
    }

    // ── FormatIssueMarkdown — direct unit tests (internal static) ────────

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

        var markdown = WriteOpenIssueContextStep.FormatIssueMarkdown(detail);

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

        var markdown = WriteOpenIssueContextStep.FormatIssueMarkdown(detail);

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

        var markdown = WriteOpenIssueContextStep.FormatIssueMarkdown(detail, isClosed: true);

        markdown.Should().Contain("status: closed");
        markdown.Should().Contain("identifier: \"99\"");
    }

    [Fact]
    public void FormatIssueMarkdown_OpenIssue_DoesNotIncludeStatusField()
    {
        var detail = new IssueDetail
        {
            Identifier = "100",
            Title = "Open task",
            Description = "In progress",
            Labels = new[] { "agent:next" }
        };

        var markdown = WriteOpenIssueContextStep.FormatIssueMarkdown(detail, isClosed: false);

        markdown.Should().NotContain("status:");
    }

    [Fact]
    public void FormatIssueMarkdown_EscapesSpecialCharacters()
    {
        var detail = new IssueDetail
        {
            Identifier = "1",
            Title = "Title with \"quotes\" and \\backslash\\ and\nnewline\ttab",
            Description = "Body",
            Labels = Array.Empty<string>()
        };

        var markdown = WriteOpenIssueContextStep.FormatIssueMarkdown(detail);

        markdown.Should().Contain("\\\"quotes\\\"");
        markdown.Should().Contain("\\\\backslash\\\\");
        markdown.Should().Contain("\\n");
        markdown.Should().Contain("\\t");
    }

    // ── IsEpicScopedRun static ──────────────────────────────────────────

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis, true)]
    [InlineData(PipelineRunType.Decomposition, true)]
    [InlineData(PipelineRunType.Implementation, false)]
    [InlineData(PipelineRunType.Review, false)]
    public void IsEpicScopedRun_ReturnsExpected(PipelineRunType runType, bool expected)
    {
        WriteOpenIssueContextStep.IsEpicScopedRun(runType).Should().Be(expected);
    }
}
