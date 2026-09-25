using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Steps;

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

    // ── Cap-skipped entry rendering (Req 5, issue #3018) ────────────────

    [Fact]
    public void FormatSummaryComment_CapSkippedEntries_RenderedAsNotCreated()
    {
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" },
            new() { Title = "Issue 2", Success = false, FailureReason = "API error" },
            new() { Title = "Issue 3 (cap)", Success = false, SkippedByCap = true, FailureReason = "Not created: plan exceeded the 2 sub-issue cap" }
        };

        // attempted=2 (excludes cap-skipped), succeeded=1, failed=1
        var summary = PostDecompositionSummaryStep.FormatSummaryComment(results, attempted: 2, succeeded: 1, failed: 1);

        // Cap-skipped entry renders with the distinct status
        summary.Should().Contain("⏭️ Not created (cap)");

        // Real failed entry still renders ❌
        summary.Should().Contain("❌ Failed");

        // Cap-skipped entry's reason is shown as the link column
        summary.Should().Contain("exceeded the 2 sub-issue cap");

        // Header counts reflect attempted=2 (cap-skipped excluded)
        summary.Should().Contain("Created:** 1/2");
        summary.Should().Contain("Failed:** 1/2");

        // Issue 3 (cap) must NOT render as ❌ — check the row content specifically
        // The cap-skipped row has "⏭️ Not created (cap)" as its status column
        summary.Should().Contain("Issue 3 (cap)");
        var capRow = summary.Split('\n').First(l => l.Contains("Issue 3 (cap)"));
        capRow.Should().Contain("⏭️ Not created (cap)");
        capRow.Should().NotContain("❌ Failed");
    }

    [Fact]
    public async Task ExecuteAsync_CapSkippedEntriesPresent_OutcomeLabelIsStillDone()
    {
        // 1 success + 1 cap-skipped → not all failed → agent:done
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1", Success = true, Identifier = "101", Url = "https://github.com/test/101" },
            new() { Title = "Issue 2 (cap)", Success = false, SkippedByCap = true, FailureReason = "Not created: plan exceeded the 1 sub-issue cap" }
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
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_AllProposalsBeyondCap_OutcomeLabelIsError()
    {
        // All entries are cap-skipped → attempted=0 → agent:error
        // TODO: FailureReason uses "the 0 sub-issue cap" which is not a realistic production value
        // (MaxDecompositionSubIssues is always ≥ 1). Use a plausible cap (e.g. 2) and 3+ proposals so
        // the test data is consistent with a real scenario and the reason string is accurate.
        var results = new List<SubIssueCreationResult>
        {
            new() { Title = "Issue 1 (cap)", Success = false, SkippedByCap = true, FailureReason = "Not created: plan exceeded the 0 sub-issue cap" },
            new() { Title = "Issue 2 (cap)", Success = false, SkippedByCap = true, FailureReason = "Not created: plan exceeded the 0 sub-issue cap" }
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
        // zero real creations attempted (all cap-skipped) → attempted=0 → allFailed=true → agent:error
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
        _issueOps.Verify(x => x.SwapLabelAsync("50", AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Never);
    }
}
