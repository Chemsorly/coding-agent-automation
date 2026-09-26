using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgent.Pipeline.UnitTests;

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

    private readonly TestMeterFactory _meterFactory = new();

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

        _executor = new AgentPhaseExecutor(_mockLogger.Object, _meterFactory);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);
    }

    public void Dispose()
    {
        _meterFactory.Dispose();
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
    public async Task Analysis_RetryExhausted_SwapsNeedsRefinementLabel()
    {
        // Agent executes but produces no output files on any attempt — exhausts all retries.
        // With MaxAnalysisRetries = 1 (set in the test constructor), ExecuteAsync is called
        // twice (attempt 0 and attempt 1). The mock returns the same result for both calls.
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("Analysis failed");
        // TODO [WARNING]: Missing Times.Exactly(2) verification on ExecuteAsync. MaxAnalysisRetries=1 means the loop
        // runs for attempt 0 and attempt 1 (two calls). Without asserting the call count, a premature exit on attempt 0
        // that still reaches FailPhaseAsync (e.g. via a different early-exit code path) would pass all assertions below
        // without actually exhausting both retries. Add:
        //   _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
        // Retry exhaustion is a semantic failure (agent did not produce required outputs), not an
        // infrastructure crash. Must label agent:needs-refinement, not agent:error.
        _mockIssueOps.Verify(o => o.SwapLabelAsync("42", AgentLabels.NeedsRefinement, It.IsAny<CancellationToken>()), Times.Once);
        // Note: The Times.Never assertion below is a weak guard — it only proves AgentLabels.Error
        // was not called, but would pass even if the production code used a third label constant.
        // The meaningful safety net is the Times.Once check on NeedsRefinement above. If stronger
        // exclusivity is needed, enumerate all other AgentLabels constants and assert Times.Never
        // for each, or capture the actual label argument and assert strict equality.
        _mockIssueOps.Verify(o => o.SwapLabelAsync("42", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Never);
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
        // TODO: No test covers the case where existingComment.Id is non-numeric (e.g. "c1").
        // long.Parse in AgentPhaseExecutor.Analysis.cs:505 would throw FormatException, propagating
        // uncaught through the catch block (which only handles non-OperationCanceledException) and
        // aborting the analysis phase. A test with a non-numeric Id should assert the observable
        // behavior (exception propagates or fallback fires). See review finding on
        // AgentPhaseExecutorAnalysisTests.cs:276.
        var comments = new[]
        {
            new IssueComment { Id = "42", Body = $"{CommentMarkers.AnalysisHeader}\nOld analysis content", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
        };

        SetupAgentWithValidAnalysis("ready");
        _mockIssueOps.Setup(o => o.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), comments, forceRefreshFromDispatch: true, CancellationToken.None);

        result.Should().BeTrue();
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(
            "42", 42L,
            It.Is<string>(body => body.Contains("<!-- agent:analysis-body-hash:")),
            It.IsAny<CancellationToken>()), Times.Once);
        _mockIssueOps.Verify(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<long>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task Analysis_Cancellation_ThrowsOperationCancelledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var ex = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, cts.Token));

        ex.Should().NotBeNull();
    }

    // --- Rework context wiring tests ---

    [Fact]
    public async Task Analysis_WithLinkedPullRequest_PromptContainsReworkContext()
    {
        _run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 99,
            BranchName = "feature/rework-branch",
            IsDraft = false,
            Url = "https://github.com/test/repo/pull/99"
        };

        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompt = req.Prompt;
                WriteValidAnalysisFiles();
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("## Rework Context");
        capturedPrompt.Should().Contain("99");
        capturedPrompt.Should().Contain("feature/rework-branch");
    }

    [Fact]
    public async Task Analysis_WithLinkedPullRequestAndForceResolvedFiles_PromptListsConflictFiles()
    {
        _run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 100,
            BranchName = "feature/conflict-branch",
            IsDraft = false,
            Url = "https://github.com/test/repo/pull/100"
        };
        _run.MergeConflictFiles = new[] { "src/Foo.cs", "src/Bar.cs" };
        _run.MergeForceResolved = true;

        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompt = req.Prompt;
                WriteValidAnalysisFiles();
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        capturedPrompt.Should().Contain("src/Foo.cs");
        capturedPrompt.Should().Contain("src/Bar.cs");
        capturedPrompt.Should().Contain("force-resolved");
    }

    [Fact]
    public async Task Analysis_WithLinkedPullRequest_NoForceResolved_PromptExcludesConflictList()
    {
        _run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 101,
            BranchName = "feature/clean-branch",
            IsDraft = false,
            Url = "https://github.com/test/repo/pull/101"
        };
        _run.MergeConflictFiles = new[] { "src/Foo.cs" }; // conflicted but NOT force-resolved
        _run.MergeForceResolved = false;

        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompt = req.Prompt;
                WriteValidAnalysisFiles();
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        capturedPrompt.Should().NotContain("force-resolved");
        capturedPrompt.Should().Contain("## Rework Context"); // rework section still present
    }

    [Fact]
    public async Task Analysis_WithLinkedPullRequestAndReviewComments_PromptReferencesConversationFile()
    {
        _run.LinkedPullRequest = new LinkedPullRequest
        {
            Number = 102,
            BranchName = "feature/reviewed-branch",
            IsDraft = false,
            Url = "https://github.com/test/repo/pull/102",
            ReviewComments = new[]
            {
                new PullRequestReviewComment
                {
                    Id = "c1",
                    Author = "reviewer",
                    Body = "Please fix the null check here.",
                    CreatedAt = DateTime.UtcNow
                }
            }
        };

        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompt = req.Prompt;
                WriteValidAnalysisFiles();
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        capturedPrompt.Should().Contain("pr-conversation-context.md");
    }

    [Fact]
    public async Task Analysis_WithoutLinkedPullRequest_PromptExcludesReworkContext()
    {
        // Fresh run with no LinkedPullRequest — must NOT contain rework context (regression guard)
        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedPrompt = req.Prompt;
                WriteValidAnalysisFiles();
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        capturedPrompt.Should().NotContain("## Rework Context");
        capturedPrompt.Should().NotContain("rework run");
    }

    // --- Analysis gate outcome metric tests ---

    [Fact]
    public async Task EvaluateAnalysisGate_NotReadyAssessment_EmitsNotReadyMetric()
    {
        // not_ready recommendation with no blocking issues — unambiguously exercises the not_ready path
        SetupAgentWithValidAnalysis("not_ready");
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.analysis.gate_outcome");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.NotReady);
        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "not_ready")));
    }

    [Fact]
    public async Task EvaluateAnalysisGate_WontDoAssessment_EmitsWontDoMetric()
    {
        SetupAgentWithValidAnalysis("wont_do");
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.analysis.gate_outcome");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);
        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "wont_do")));
    }

    [Fact]
    public async Task EvaluateAnalysisGate_ReadyAssessment_EmitsReadyMetricAndReturnsTrue()
    {
        SetupAgentWithValidAnalysis("ready");
        using var collector = new MetricCollector<long>(_meterFactory, PipelineTelemetry.SourceName, "pipeline.analysis.gate_outcome");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeTrue();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        collector.GetMeasurementSnapshot().Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("outcome", "ready")));
    }

    // --- Gate FailureCategory tests (issue #2956) ---

    /// <summary>
    /// Needs-refinement gate must set run.FailureCategory = GateRejected so the HTTP reporter
    /// persists GateRejected (not AgentError) via BuildCompletionPayload.FailureCategory.
    /// </summary>
    [Fact]
    public async Task HandleNotReadyGate_SetsRunFailureCategoryToGateRejected()
    {
        SetupAgentWithValidAnalysis("not_ready", blockingIssues: new[] { "Needs more detail" });

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        // TODO: [WARNING] This only asserts the in-memory run.FailureCategory value. The acceptance
        // criterion is "needs-refinement gate outcomes are persisted with the new gate FailureReason,
        // not AgentError". A bug in BuildCompletionPayload that ignores run.FailureCategory (e.g.,
        // using a different field) would not be caught here. For full coverage, add a test in
        // HttpPrimaryCompletionReporterTests that passes a payload with FailureCategory=GateRejected
        // and asserts FailureReason="GateRejected" is posted. See ReportCompletionAsync_GateRejected_
        // PayloadCategory_PersistsGateRejectedFailureReason (already added in issue #2956 follow-up).
        // (Review finding: TestQualityReviewer [WARNING] — issue #2956)
        _run.FailureCategory.Should().Be(FailureReason.GateRejected,
            "needs-refinement gate must categorise the run as GateRejected, not AgentError");
    }

    /// <summary>
    /// Won't-do gate must set run.FailureCategory = GateRejected. Won't-do uses
    /// PipelineStep.Completed (Succeeded status); HttpPrimaryCompletionReporter persists
    /// GateRejected via the payload.FailureCategory fallback (issue #2956).
    /// </summary>
    [Fact]
    public async Task HandleWontDoGate_SetsRunFailureCategoryToGateRejected()
    {
        SetupAgentWithValidAnalysis("wont_do");

        await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        _run.FailureCategory.Should().Be(FailureReason.GateRejected,
            "won't-do gate must categorise the run as GateRejected; HTTP reporter persists it via FailureCategory fallback");
    }

    [Fact]
    public async Task Analysis_EnableNativeImagePartsTrue_ImagePathsPassedToAgent()
    {
        // Arrange: non-null DownloadedImages + EnableNativeImageParts = true → ImagePaths must be forwarded.
        // We cannot use SetupAgentWithValidAnalysis() directly because it captures AgentRequest internally
        // and doesn't expose req. We set up the mock manually, writing the required analysis files in the
        // Callback while also capturing the request.
        var testImage = new DownloadedImage
        {
            LocalPath = "/tmp/img.png",
            LocalFilename = "img.png",
            Reference = new ImageReference
            {
                Url = "https://example.com/img.png",
                AltText = "test",
                SourceType = ImageSourceType.Body,
                SourceIndex = 0
            },
            FileSizeBytes = 1024,
            MimeType = "image/png"
        };

        AgentRequest? capturedRequest = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedRequest = req;
                // Write the analysis files the executor requires before it can continue
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath),
                    new string('x', PipelineConstants.MinAnalysisLength + 100));
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
                    JsonSerializer.Serialize(new { recommendation = "ready", reason = "test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() }));
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var context = new AgentPhaseContext
        {
            Run = _run,
            Config = _config with { EnableNativeImageParts = true },
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = null,
            Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Test description", Labels = new[] { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "Test requirements", AcceptanceCriteria = new[] { "AC1", "AC2" } },
            DownloadedImages = new[] { testImage }
        };

        // Act
        await _executor.ExecuteAnalysisPhaseAsync(context, Array.Empty<IssueComment>(), false, CancellationToken.None);

        // TODO: [WARNING] If ExecuteAnalysisPhaseAsync short-circuits before calling ExecuteAsync (e.g., a precondition
        // failure), capturedRequest remains null and the null-check below catches it, but the subsequent
        // capturedRequest!.ImagePaths access produces a less informative crash. Consider adding:
        //   _mockAgent.Verify(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.AtLeastOnce());
        // before the ImagePaths assertions to produce a clean failure message.
        // TODO: [WARNING] Missing boundary case: EnableNativeImageParts=true with DownloadedImages=null should yield
        // ImagePaths=null without throwing. Add a [Theory] row or separate test to cover this path.

        // Assert: flag true → images forwarded
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ImagePaths.Should().NotBeNull();
        capturedRequest.ImagePaths.Should().Contain("/tmp/img.png");
    }

    [Fact]
    public async Task Analysis_EnableNativeImagePartsFalse_ImagePathsIsNull()
    {
        // Arrange: non-null DownloadedImages + EnableNativeImageParts = false → ImagePaths must be null
        var testImage = new DownloadedImage
        {
            LocalPath = "/tmp/img.png",
            LocalFilename = "img.png",
            Reference = new ImageReference
            {
                Url = "https://example.com/img.png",
                AltText = "test",
                SourceType = ImageSourceType.Body,
                SourceIndex = 0
            },
            FileSizeBytes = 1024,
            MimeType = "image/png"
        };

        AgentRequest? capturedRequest = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                capturedRequest = req;
                var agentDir = Path.Combine(_workspacePath, ".agent");
                Directory.CreateDirectory(agentDir);
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisFilePath),
                    new string('x', PipelineConstants.MinAnalysisLength + 100));
                File.WriteAllText(
                    Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
                    JsonSerializer.Serialize(new { recommendation = "ready", reason = "test", concerns = Array.Empty<string>(), blockingIssues = Array.Empty<string>() }));
            })
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var context = new AgentPhaseContext
        {
            Run = _run,
            Config = _config with { EnableNativeImageParts = false },
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = null,
            Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Test description", Labels = new[] { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "Test requirements", AcceptanceCriteria = new[] { "AC1", "AC2" } },
            DownloadedImages = new[] { testImage }
        };

        // Act
        await _executor.ExecuteAnalysisPhaseAsync(context, Array.Empty<IssueComment>(), false, CancellationToken.None);

        // TODO: [WARNING] Same structural concern as the "true" variant above: add a Verify(Times.AtLeastOnce())
        // before the ImagePaths assertions so a short-circuit produces a clean failure rather than a null-deref crash.

        // Assert: flag false → ImagePaths suppressed, but context.DownloadedImages untouched
        capturedRequest.Should().NotBeNull();
        capturedRequest!.ImagePaths.Should().BeNull();
        context.DownloadedImages.Should().NotBeNull("context.DownloadedImages must remain populated when EnableNativeImageParts = false");
        context.DownloadedImages!.Should().Contain(testImage);
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

    /// <summary>Writes minimal valid analysis files so the executor can complete successfully.</summary>
    private void WriteValidAnalysisFiles(string recommendation = "ready")
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
            blockingIssues = Array.Empty<string>()
        };
        File.WriteAllText(
            Path.Combine(_workspacePath, AgentWorkspacePaths.AnalysisAssessmentFilePath),
            JsonSerializer.Serialize(assessment));
    }
}
