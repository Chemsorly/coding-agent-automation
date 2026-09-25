using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="PostDecompositionPlanStep"/>.
/// Tests update vs post logic, failure handling, and marker identification.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 3.8, 14.3
/// </summary>
public class PostDecompositionPlanStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public PostDecompositionPlanStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"post-plan-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
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
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    private PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.DecompositionAnalysis,
        WorkspacePath = _workspacePath
    };

    private void WritePlanFile(string content)
    {
        var planPath = Path.Combine(_workspacePath, AgentWorkspacePaths.DecompositionPlanFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(planPath)!);
        File.WriteAllText(planPath, content);
    }

    [Fact]
    public async Task ExecuteAsync_PlanFileNotFound_ReturnsStop()
    {
        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_PlanFileTooShort_ReturnsStop()
    {
        WritePlanFile("Short");

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_NoExistingComment_PostsNewComment()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.PostCommentAsync("42",
            It.Is<string>(body => body.Contains(CommentMarkers.DecompositionPlan)),
            It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(x => x.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ExistingPlanComment_UpdatesInsteadOfPosting()
    {
        // TODO: No test covers the case where existingComment.Id is non-numeric (e.g. "comment-99" —
        // the exact value used in this test before the long migration, visible in the diff context).
        // long.Parse in PostDecompositionPlanStep.cs:64 would throw FormatException inside TryCriticalAsync,
        // aborting the pipeline step rather than falling back to PostCommentAsync. A test asserting the
        // observable behavior (StepResult.Stop, or a FormatException-derived abort) would lock in this
        // behavior and prevent a silent-failure regression. See review finding on
        // PostDecompositionPlanStepTests.cs:131.
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        var existingComment = new IssueComment
        {
            Id = "99",
            Body = $"{CommentMarkers.DecompositionPlan}\n\nOld plan content",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment> { existingComment });
        _issueOps.Setup(x => x.UpdateCommentAsync("42", 99L, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.UpdateCommentAsync("42", 99L,
            It.Is<string>(body => body.Contains(CommentMarkers.DecompositionPlan)),
            It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(x => x.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_MultiplePlanComments_UsesMostRecent()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        var olderComment = new IssueComment
        {
            Id = "1",
            Body = $"{CommentMarkers.DecompositionPlan}\n\nOlder plan",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };
        var newerComment = new IssueComment
        {
            Id = "5",
            Body = $"{CommentMarkers.DecompositionPlan}\n\nNewer plan",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment> { olderComment, newerComment });
        _issueOps.Setup(x => x.UpdateCommentAsync("42", 5L, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.UpdateCommentAsync("42", 5L, It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PostingFails_ReturnsStop()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API error"));

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_Success_SwapsLabelToEpicReview()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()), Times.Once);
        context.Run.FinalLabel.Should().Be(AgentLabels.EpicReview);
    }

    [Fact]
    public async Task ExecuteAsync_SwapLabelThrows_ReturnsStepResultContinueAndSetsFinalLabel()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("transient API error"));

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.FinalLabel.Should().Be(AgentLabels.EpicReview);
    }

    [Fact]
    public async Task ExecuteAsync_SwapLabelThrowsOperationCanceledException_Propagates()
    {
        WritePlanFile("This is a valid decomposition plan with enough content to pass validation.");

        _issueOps.Setup(x => x.ListCommentsAsync("42", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync("42", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("42", AgentLabels.EpicReview, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var run = CreateRun();
        var context = BuildContext(run);
        var step = new PostDecompositionPlanStep();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => step.ExecuteAsync(context, CancellationToken.None));

        // TODO: also assert context.Run.FinalLabel == AgentLabels.EpicReview here — FinalLabel is set before
        // the SwapLabelAsync call so it should be set even on the OCE path. Without this assertion, a regression
        // that moves FinalLabel assignment to after the call would go undetected on this path.
    }

    [Fact]
    public void FormatPlanComment_MarkerIsFirstLine()
    {
        var comment = PostDecompositionPlanStep.FormatPlanComment("Test plan content here");

        comment.Should().StartWith(CommentMarkers.DecompositionPlan);
    }

    [Fact]
    public void FormatPlanComment_ContainsApprovalInstructions()
    {
        var comment = PostDecompositionPlanStep.FormatPlanComment("Test plan content here");

        comment.Should().Contain("agent:epic-approved");
        comment.Should().Contain("agent:epic-review");
    }

    [Fact]
    public void FindMostRecentPlanComment_NoComments_ReturnsNull()
    {
        var result = PostDecompositionPlanStep.FindMostRecentPlanComment(new List<IssueComment>());

        result.Should().BeNull();
    }

    [Fact]
    public void FindMostRecentPlanComment_NoMatchingComments_ReturnsNull()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = "Regular comment", Author = "user", CreatedAt = DateTime.UtcNow }
        };

        var result = PostDecompositionPlanStep.FindMostRecentPlanComment(comments);

        result.Should().BeNull();
    }

    [Fact]
    public void FindMostRecentPlanComment_MultipleMatches_ReturnsMostRecent()
    {
        var comments = new List<IssueComment>
        {
            new() { Id = "1", Body = $"{CommentMarkers.DecompositionPlan}\nOld", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-2) },
            new() { Id = "2", Body = "Regular comment", Author = "user", CreatedAt = DateTime.UtcNow.AddHours(-1) },
            new() { Id = "3", Body = $"{CommentMarkers.DecompositionPlan}\nNew", Author = "bot", CreatedAt = DateTime.UtcNow }
        };

        var result = PostDecompositionPlanStep.FindMostRecentPlanComment(comments);

        result.Should().NotBeNull();
        result!.Id.Should().Be("3");
    }

    // ── TryCountPlanSubIssues ────────────────────────────────────────────

    [Fact]
    public void TryCountPlanSubIssues_WellFormedTable_ReturnsRowCount()
    {
        var plan = """
            # Plan
            
            | # | Title | Scope | Files | Dependencies | Verification |
            |---|-------|-------|-------|--------------|--------------|
            | 1 | Add auth | Auth module | 3 | None | Tests pass |
            | 2 | Add tests | Test project | 2 | Add auth | CI green |
            | 3 | Update docs | README | 1 | None | Build ok |
            """;

        var count = PostDecompositionPlanStep.TryCountPlanSubIssues(plan);

        count.Should().Be(3);
    }

    [Fact]
    public void TryCountPlanSubIssues_NoTable_ReturnsNull()
    {
        var plan = "# Plan\n\nThis is just a text plan with no table.";

        var count = PostDecompositionPlanStep.TryCountPlanSubIssues(plan);

        count.Should().BeNull();
    }

    [Fact]
    public void TryCountPlanSubIssues_TableWithoutTitleColumn_ReturnsNull()
    {
        var plan = """
            | # | Scope | Files |
            |---|-------|-------|
            | 1 | Auth  | 3     |
            """;

        var count = PostDecompositionPlanStep.TryCountPlanSubIssues(plan);

        count.Should().BeNull();
    }

    [Fact]
    public void TryCountPlanSubIssues_TableWithoutHashColumn_ReturnsNull()
    {
        var plan = """
            | Title | Scope | Files |
            |-------|-------|-------|
            | Auth  | Auth  | 3     |
            """;

        var count = PostDecompositionPlanStep.TryCountPlanSubIssues(plan);

        count.Should().BeNull();
    }

    [Fact]
    public void TryCountPlanSubIssues_EmptyTable_ReturnsZero()
    {
        var plan = """
            | # | Title |
            |---|-------|
            """;

        var count = PostDecompositionPlanStep.TryCountPlanSubIssues(plan);

        count.Should().Be(0);
    }

    // TODO: Missing boundary condition test — a table with no separator row (header immediately followed
    // by a data row, no |---| line). The production code only advances dataStart when it finds "---";
    // without a separator, dataStart points to the data row, which is correct, but the separator detection
    // path is exercised only with well-formed tables. Add:
    //   TryCountPlanSubIssues_TableWithoutSeparatorRow_StillCountsDataRows

    // TODO: Missing integration path — TryCountPlanSubIssues returning 0 (parseable-but-empty table)
    // is exercised by TryCountPlanSubIssues_EmptyTable_ReturnsZero above, but there is no ExecuteAsync
    // test that verifies a count of 0 with a positive cap does not produce a spurious warning. A count of 0
    // satisfies subIssueCount.Value > cap == false for any positive cap, so no warning should be emitted.
    // Add: ExecuteAsync_PlanWithEmptyTable_NoWarning.

    // ── Cap warning in ExecuteAsync ──────────────────────────────────────

    private PipelineStepContext BuildContextWithCap(PipelineRun run, int maxSubIssues)
    {
        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp", MaxDecompositionSubIssues = maxSubIssues },
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
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    private static string MakePlanWithTable(int rowCount)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Decomposition Plan");
        sb.AppendLine();
        sb.AppendLine("| # | Title | Scope | Files | Dependencies | Verification |");
        sb.AppendLine("|---|-------|-------|-------|--------------|--------------|");
        for (var i = 1; i <= rowCount; i++)
            sb.AppendLine($"| {i} | Issue {i} | Scope {i} | 2 | None | Tests pass |");
        return sb.ToString();
    }

    [Fact]
    public async Task ExecuteAsync_PlanExceedsCap_CommentContainsWarning()
    {
        const int cap = 3;
        // Plan has cap+1=4 rows — exceeds the cap of 3
        WritePlanFile(MakePlanWithTable(cap + 1));

        string? capturedBody = null;
        _issueOps.Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => capturedBody = body)
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContextWithCap(run, cap);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain("Warning", because: "plan exceeds cap so a warning must be prepended");
        // TODO: Assertions below check for bare digits "4" and "3" which also appear in table row content
        // (e.g. "| 4 | Issue 4 |", "| 3 | Issue 3 |"), making these assertions too weak — they would pass
        // even if the warning text omitted the numbers. Strengthen by asserting specific warning phrases,
        // e.g. capturedBody.Should().Contain("contains 4 sub-issues") and .Contain("cap is 3").
        capturedBody.Should().Contain("4", because: "actual count (4) must be named in the warning");
        capturedBody.Should().Contain("3", because: "the cap (3) must be named in the warning");
    }

    [Fact]
    public async Task ExecuteAsync_PlanAtCap_NoWarning()
    {
        const int cap = 3;
        // Plan has exactly cap rows — at the cap, not over it
        WritePlanFile(MakePlanWithTable(cap));

        string? capturedBody = null;
        _issueOps.Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => capturedBody = body)
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContextWithCap(run, cap);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        capturedBody.Should().NotBeNull();
        capturedBody.Should().NotContain("Warning",
            because: "plan is at the cap (not over it) so no warning should be prepended");
    }

    [Fact]
    public async Task ExecuteAsync_PlanWithinCap_NoWarning()
    {
        const int cap = 5;
        // Plan has 2 rows — well within cap
        WritePlanFile(MakePlanWithTable(2));

        string? capturedBody = null;
        _issueOps.Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => capturedBody = body)
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContextWithCap(run, cap);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        capturedBody.Should().NotBeNull();
        capturedBody.Should().NotContain("Warning",
            because: "plan is within the cap so no warning should be prepended");
    }

    [Fact]
    public async Task ExecuteAsync_PlanWithoutParseableTable_NoWarning()
    {
        // Plan is valid text but has no markdown table → fail-open, no warning
        WritePlanFile("# Decomposition Plan\n\nThis is just text with no sub-issue table.");

        string? capturedBody = null;
        _issueOps.Setup(x => x.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<IssueComment>());
        _issueOps.Setup(x => x.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => capturedBody = body)
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun();
        var context = BuildContextWithCap(run, 3);
        var step = new PostDecompositionPlanStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        capturedBody.Should().NotBeNull();
        capturedBody.Should().NotContain("Warning",
            because: "table could not be parsed so the step proceeds without a warning (fail-open)");
    }

    [Fact]
    public void FormatPlanComment_WithWarning_WarningAppearsAfterMarkerBeforePlanHeading()
    {
        const string warning = "> ⚠️ **Warning: this plan exceeds the cap.**";
        var comment = PostDecompositionPlanStep.FormatPlanComment("Test plan content here", warning);

        // Marker must still be first line
        comment.Should().StartWith(CommentMarkers.DecompositionPlan);

        // Warning appears between the marker and the plan heading
        var markerIndex = comment.IndexOf(CommentMarkers.DecompositionPlan, StringComparison.Ordinal);
        var warningIndex = comment.IndexOf(warning, StringComparison.Ordinal);
        var headingIndex = comment.IndexOf("## 🧩 Decomposition Plan", StringComparison.Ordinal);

        warningIndex.Should().BeGreaterThan(markerIndex);
        headingIndex.Should().BeGreaterThan(warningIndex);
    }
}
