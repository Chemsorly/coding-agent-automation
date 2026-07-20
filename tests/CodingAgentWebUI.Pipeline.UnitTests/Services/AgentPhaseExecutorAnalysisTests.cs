using System.Text.Json;
using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Isolated unit tests for <see cref="AgentPhaseExecutor.ExecuteAnalysisPhaseAsync"/>.
/// Tests warm-up, prompt dispatch, retry logic, confidence gate assessment, and the existing-analysis skip path.
/// </summary>
public class AgentPhaseExecutorAnalysisTests : IDisposable
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly AgentPhaseExecutor _executor;
    private readonly string _workspacePath;

    public AgentPhaseExecutorAnalysisTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _workspacePath = Path.Combine(Path.GetTempPath(), $"test-analysis-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);

        _run = new PipelineRun
        {
            RunId = "test-run-analysis",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = _workspacePath
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1),
            MaxAnalysisRetries = 1,
            AnalysisReviewEnabled = false
        };

        _executor = new AgentPhaseExecutor(_mockLogger.Object);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
        // TODO: Add a test that verifies PostCommentAsync is called with content containing
        // the <!-- agent:analysis-body-hash:{hash} --> marker. Currently all tests use It.IsAny<string>()
        // for the comment body, so removing the hash embedding in production would go undetected.
        // TODO: Add a test exercising the forceRefreshFromDispatch = true path to verify the
        // dispatch-level staleness merge logic (bool forceRefresh = forceRefreshFromDispatch || ...) works.
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspacePath, recursive: true); } catch { }
    }

    [Fact]
    public async Task Analysis_WarmUpCalled_BeforeExecution()
    {
        SetupAgentWithValidAnalysis("ready");

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        _mockAgent.Verify(a => a.EnsureSessionAsync(_workspacePath, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analysis_ReadyAssessment_ReturnsTrue()
    {
        SetupAgentWithValidAnalysis("ready");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Analysis_NotReadyAssessment_ReturnsFalseAndSwapsLabel()
    {
        SetupAgentWithValidAnalysis("not_ready", blockingIssues: new[] { "Missing API spec" });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _mockIssueOps.Verify(o => o.SwapLabelAsync("42", AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analysis_WontDoAssessment_ReturnsFalseAndSwapsLabel()
    {
        SetupAgentWithValidAnalysis("wont_do");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _mockIssueOps.Verify(o => o.SwapLabelAsync("42", AgentLabels.WontDo, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analysis_ReadyWithBlockingIssues_TriggersNotReadyPath()
    {
        // Even if recommendation is "ready", non-empty BlockingIssues forces not_ready
        SetupAgentWithValidAnalysis("ready", blockingIssues: new[] { "Depends on #123" });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _mockIssueOps.Verify(o => o.SwapLabelAsync("42", AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Analysis_FileNotFound_RetriesThenFails()
    {
        // Agent executes but produces no output files — triggers retry
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("Analysis failed");
    }

    [Fact]
    public async Task Analysis_FileTooShort_RetriesThenFails()
    {
        // Agent writes a file that's too short
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath), "short");
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("Analysis failed");
    }

    [Fact]
    public async Task Analysis_NonZeroExitWithValidFiles_Succeeds()
    {
        // Non-zero exit code does NOT trigger retry if files are valid
        SetupAgentWithValidAnalysis("ready", exitCode: 1);

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task Analysis_NullRecommendation_RetriesThenFails()
    {
        // Agent writes assessment file with explicit null recommendation — treated as incomplete
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath),
                    new string('x', PipelineConstants.MinAnalysisLength + 100));
                // Write assessment with explicit null recommendation value
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
                    """{"recommendation": null, "reason": "some analysis", "concerns": []}""");
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("Analysis failed");
        _run.FailureReason.Should().Contain("recommendation");
    }

    [Fact]
    public async Task Analysis_EmptyRecommendation_RetriesThenFails()
    {
        // Agent writes assessment with empty string recommendation — treated as incomplete
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath),
                    new string('x', PipelineConstants.MinAnalysisLength + 100));
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
                    """{"recommendation": "", "reason": "forgot to fill this in"}""");
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("Analysis failed");
        _run.FailureReason.Should().Contain("recommendation");
    }

    [Fact]
    public async Task Analysis_ExistingAnalysisComment_SkipsAgentExecution()
    {
        var comments = new[]
        {
            new IssueComment { Id = "1", Body = $"{CommentMarkers.AnalysisHeader}\nExisting analysis content that is long enough to satisfy checks", Author = "bot", CreatedAt = DateTime.UtcNow }
        };

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), comments, false, CancellationToken.None);

        // EnsureSessionAsync called (warm-up) but ExecuteAsync never called
        _mockAgent.Verify(a => a.EnsureSessionAsync(_workspacePath, It.IsAny<CancellationToken>()), Times.Once);
        _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Never);
        _run.AnalysisSkipped.Should().BeTrue();
    }

    [Fact]
    public async Task Analysis_ForceRefresh_ExistingComment_UpdatesInsteadOfPosting()
    {
        // Existing analysis comment present + force-refresh → should update, not post new
        // TODO: Add additional tests for force-refresh with not-ready/won't-do assessments (update path is also hit on those branches)
        // TODO: Verify ExecuteAsync is called on the agent provider (to confirm force-refresh bypasses the skip-analysis logic)
        // TODO: Add boundary test with multiple existing analysis comments to verify only the most recent one's ID is passed to UpdateCommentAsync
        var comments = new[]
        {
            new IssueComment { Id = "comment-42", Body = $"{CommentMarkers.AnalysisHeader}\nOld analysis content", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
        };

        SetupAgentWithValidAnalysis("ready");
        _mockIssueOps.Setup(o => o.UpdateCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), comments, forceRefreshFromDispatch: true, CancellationToken.None);

        result.Should().BeTrue();
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(
            "42", "comment-42",
            It.Is<string>(body => body.Contains("<!-- agent:analysis-body-hash:")),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockIssueOps.Verify(o => o.PostCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Analysis_NoExistingComment_PostsNewComment()
    {
        // No existing analysis comment → should post new, not update
        SetupAgentWithValidAnalysis("ready");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeTrue();
        _mockIssueOps.Verify(o => o.PostCommentAsync(
            "42",
            It.Is<string>(body => body.Contains("<!-- agent:analysis-body-hash:")),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Analysis_Cancellation_ThrowsOperationCancelledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, cts.Token));

        ex.Should().NotBeNull();
    }

    private AgentPhaseContext BuildContext()
    {
        return new AgentPhaseContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = null,
            Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Test description", Labels = new[] { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "Test requirements", AcceptanceCriteria = new[] { "AC1", "AC2" } }
        };
    }

    private void SetupAgentWithValidAnalysis(string recommendation, int exitCode = 0, string[]? blockingIssues = null)
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, _) =>
            {
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath),
                    new string('x', PipelineConstants.MinAnalysisLength + 100));
                var assessment = new
                {
                    recommendation,
                    reason = "test",
                    concerns = Array.Empty<string>(),
                    blockingIssues = blockingIssues ?? Array.Empty<string>()
                };
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
                    JsonSerializer.Serialize(assessment));
            })
            .ReturnsAsync(new AgentResult { ExitCode = exitCode, OutputLines = Array.Empty<string>() });
    }
}
