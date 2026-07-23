using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="PostDecompositionSummaryStep"/>.
/// Tests all-failed → error, partial → done, zero attempted → error, and summary post failure handling.
/// Feature: 027-epic-decomposition-pipeline, Requirements: 10.3, 10.4, 10.6
/// </summary>
public class PostDecompositionSummaryStepTests
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

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

    private PipelineRun CreateRun(IReadOnlyList<SubIssueCreationResult> results) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "50",
        IssueTitle = "Test Epic",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        RunType = PipelineRunType.Decomposition,
        WorkspacePath = "/tmp/test",
        SubIssueResults = results
    };

    [Fact]
    public async Task ExecuteAsync_AllSucceeded_SwapsLabelToDone()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" },
            new() { Title = "Issue 2", Success = true, Identifier = "102", Url = "https://github.com/test/102" }
        };

        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_AllFailed_SwapsLabelToError()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = false, FailureReason = "API error" },
            new() { Title = "Issue 2", Success = false, FailureReason = "Timeout" }
        };

        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ZeroAttempted_SwapsLabelToError()
    {
        var results = new List<SubIssueCreationResult>();

        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PartialSuccess_SwapsLabelToDone()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" },
            new() { Title = "Issue 2", Success = false, FailureReason = "API error" },
            new() { Title = "Issue 3", Success = true, Identifier = "103", Url = "https://github.com/test/103" }
        };

        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_SummaryPostFails_ProceedsWithLabelSwap()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" }
        };

        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        // Should still succeed (non-fatal) and swap label
        result.Should().Be(StepResult.Continue);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PostsSummaryWithMarker()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" }
        };

        string? capturedBody = null;
        _issueOps.Setup(x => x.PostCommentAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Callback<IssueIdentifier, string, CancellationToken>((_, body, _) => capturedBody = body)
            .ReturnsAsync((string?)null);
        _issueOps.Setup(x => x.SwapLabelAsync("50", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var run = CreateRun(results);
        var context = BuildContext(run);
        var step = new PostDecompositionSummaryStep();

        await step.ExecuteAsync(context, CancellationToken.None);

        capturedBody.Should().NotBeNull();
        capturedBody.Should().Contain(CommentMarkers.DecompositionSummary);
    }

    [Fact]
    public void FormatSummaryComment_ZeroAttempted_ShowsWarning()
    {
        var results = new List<SubIssueCreationResult>();

        var summary = PostDecompositionSummaryStep.FormatSummaryComment(results, 0, 0, 0);

        summary.Should().Contain("No sub-issues were attempted");
        summary.Should().Contain(CommentMarkers.DecompositionSummary);
    }

    [Fact]
    public void FormatSummaryComment_MixedResults_ShowsTable()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" },
            new() { Title = "Issue 2", Success = false, FailureReason = "Timeout" }
        };

        var summary = PostDecompositionSummaryStep.FormatSummaryComment(results, 2, 1, 1);

        summary.Should().Contain("Created:** 1/2");
        summary.Should().Contain("Failed:** 1/2");
        summary.Should().Contain("✅ Created");
        summary.Should().Contain("❌ Failed");
        summary.Should().Contain("Timeout");
    }
}
