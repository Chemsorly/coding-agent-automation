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
}
