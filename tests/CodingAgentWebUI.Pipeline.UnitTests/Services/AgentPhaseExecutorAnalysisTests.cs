using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Text.Json;
using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Isolated unit tests for <see cref="AgentPhaseExecutor.ExecuteAnalysisPhaseAsync"/>.
/// Tests warm-up, prompt dispatch, retry logic, confidence gate assessment, and the existing-analysis skip path.
/// </summary>
/// <remarks>
/// This class is in <see cref="Collection"/>("Metrics") to prevent concurrent <see cref="MeterListener"/>
/// contention with other metric tests that listen on the same static <see cref="PipelineTelemetry.Meter"/>.
/// </remarks>
[Collection("Metrics")]
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

    // MeterListener for metric-assertion tests
    private readonly MeterListener _meterListener = new();
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];

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
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockIssueOps.Setup(o => o.PostCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string?)null);

        // Wire up the MeterListener to capture all long-valued counters from the pipeline meter
        _meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };
        _meterListener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });
        _meterListener.Start();
    }

    public void Dispose()
    {
        _meterListener.Dispose();
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
        var comments = new[]
        {
            new IssueComment { Id = "comment-42", Body = $"{CommentMarkers.AnalysisHeader}\nOld analysis content", Author = "bot", CreatedAt = DateTime.UtcNow.AddHours(-1) }
        };

        SetupAgentWithValidAnalysis("ready");
        _mockIssueOps.Setup(o => o.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), comments, forceRefreshFromDispatch: true, CancellationToken.None);

        result.Should().BeTrue();
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(
            "42", "comment-42",
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
        _mockIssueOps.Verify(o => o.UpdateCommentAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
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

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.NotReady);
        _counters.Should().Contain(c =>
            c.Name == "pipeline.analysis.gate_outcome"
            && c.Tags.Contains(new KeyValuePair<string, object?>("outcome", "not_ready")));
    }

    [Fact]
    public async Task EvaluateAnalysisGate_WontDoAssessment_EmitsWontDoMetric()
    {
        SetupAgentWithValidAnalysis("wont_do");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeFalse();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);
        _counters.Should().Contain(c =>
            c.Name == "pipeline.analysis.gate_outcome"
            && c.Tags.Contains(new KeyValuePair<string, object?>("outcome", "wont_do")));
    }

    [Fact]
    public async Task EvaluateAnalysisGate_ReadyAssessment_EmitsReadyMetricAndReturnsTrue()
    {
        SetupAgentWithValidAnalysis("ready");

        var result = await _executor.ExecuteAnalysisPhaseAsync(BuildContext(), Array.Empty<IssueComment>(), false, CancellationToken.None);

        result.Should().BeTrue();
        _run.AnalysisRecommendation.Should().Be(AnalysisGateResult.Ready);
        _counters.Should().Contain(c =>
            c.Name == "pipeline.analysis.gate_outcome"
            && c.Tags.Contains(new KeyValuePair<string, object?>("outcome", "ready")));
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
