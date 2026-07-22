using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for failure feedback integration in QualityGateExecutor.
/// Validates: Requirements 3.1, 3.9, 3.10, 8.1, 8.3, 8.4
/// </summary>
public class QualityGateExecutorFeedbackTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly QualityGateExecutor _orchestrator;

    private static readonly QualityGateReport FailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build failed" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "3 tests failed" }
    };

    public QualityGateExecutorFeedbackTests()
    {
        _mockValidator = new Mock<IQualityGateValidator>();
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockRepoProvider = new Mock<IRepositoryProvider>();
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-feedback",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue for Feedback",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-feedback-test-{Guid.NewGuid():N}")
        };

        // MaxRetries = 0 so the retry loop is immediately exhausted
        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            MaxRetries = 0,
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _orchestrator = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // Default: callbacks complete successfully
        _mockCallbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.RemoveAllAgentLabels(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreatePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.FinalizePullRequest(It.IsAny<PipelineRun>(), It.IsAny<QualityGateReport>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.CreateDraftPrIfNotExists(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: issue ops complete successfully
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default: history service returns empty list
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // Default: agent health status
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });
    }

    [Fact]
    public async Task FeedbackCallExecutes_AfterMaxRetriesExhausted()
    {
        // Arrange: validator always fails, MaxRetries = 0 so immediately exhausted
        SetupValidatorAlwaysFails();
        SetupAgentReturnsValidFeedback();

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: agent was called with a prompt containing "Pipeline Failure Feedback"
        _mockAgent.Verify(a => a.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback") && r.UseResume),
            It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task FeedbackTimeout_ProducesFallbackWithTimedOutReason()
    {
        // Arrange: validator always fails, feedback agent call times out
        SetupValidatorAlwaysFails();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException("The operation was canceled."));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: run.Feedback is set with timeout fallback
        _run.Feedback.Should().NotBeNull();
        _run.Feedback!.Outcome.Should().Be(FeedbackOutcome.Failure);
        _run.Feedback.Harness.StuckReason.Should().Be("Feedback collection timed out");
    }

    [Fact]
    public async Task FeedbackException_ProducesFallbackWithExceptionMessage()
    {
        // Arrange: validator always fails, feedback agent call throws
        SetupValidatorAlwaysFails();

        var exceptionMessage = "Agent process crashed unexpectedly";
        _mockAgent.Setup(a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException(exceptionMessage));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: run.Feedback is set with exception fallback
        _run.Feedback.Should().NotBeNull();
        _run.Feedback!.Outcome.Should().Be(FeedbackOutcome.Failure);
        _run.Feedback.Harness.StuckReason.Should().Contain(exceptionMessage);
        _run.Feedback.Harness.StuckReason.Should().Contain("Feedback collection failed");
    }

    [Fact]
    public async Task FeedbackCall_DoesNotCountAgainstMaxRetries()
    {
        // Arrange: MaxRetries = 0, validator always fails
        SetupValidatorAlwaysFails();
        SetupAgentReturnsValidFeedback();

        var context = BuildContext();
        var retryCountBefore = _run.RetryCount;

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: RetryCount was not incremented by the feedback call
        // With MaxRetries = 0, the retry loop doesn't execute, so RetryCount stays at 0
        _run.RetryCount.Should().Be(retryCountBefore);
    }

    [Fact]
    public async Task Pipeline_ContinuesToDraftPR_RegardlessOfFeedbackOutcome_WhenFeedbackSucceeds()
    {
        // Arrange: validator always fails, feedback succeeds
        SetupValidatorAlwaysFails();
        SetupAgentReturnsValidFeedback();

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: FinalizePullRequest was called with isDraft = true
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pipeline_ContinuesToDraftPR_RegardlessOfFeedbackOutcome_WhenFeedbackTimesOut()
    {
        // Arrange: validator always fails, feedback times out
        SetupValidatorAlwaysFails();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException("Timed out"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: FinalizePullRequest was still called with isDraft = true
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Pipeline_ContinuesToDraftPR_RegardlessOfFeedbackOutcome_WhenFeedbackThrows()
    {
        // Arrange: validator always fails, feedback throws exception
        SetupValidatorAlwaysFails();

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("Unexpected error"));

        var context = BuildContext();

        // Act
        await _orchestrator.ProceedToQualityGatesAsync(context, CancellationToken.None);

        // Assert: FinalizePullRequest was still called with isDraft = true
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void SetupValidatorAlwaysFails()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(FailingReport);
    }

    private void SetupAgentReturnsValidFeedback()
    {
        var feedbackJson = """
            ```json
            {
                "harness": {
                    "category": "compilation failure",
                    "stuckReason": "Build failed due to missing dependency",
                    "missingContext": ["package.json"],
                    "suggestions": ["Add dependency resolution step"]
                }
            }
            ```
            """;

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.Is<AgentRequest>(r => r.Prompt.Contains("Pipeline Failure Feedback")),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = new[] { feedbackJson } });
    }

    private QualityGateContext BuildContext()
    {
        return new QualityGateContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            RepoProvider = _mockRepoProvider.Object,
            QualityGateConfigs = new[]
            {
                new QualityGateConfiguration
                {
                    DisplayName = "Test QGC",
                    CompilationCommand = "dotnet",
                    CompilationArguments = new[] { "build" },
                    TestCommand = "dotnet",
                    TestArguments = new[] { "test" }
                }
            },
            Issue = new IssueDetail
            {
                Identifier = "42",
                Title = "Test Issue for Feedback",
                Description = "This is a test issue description",
                Labels = new[] { "bug" }
            }
        };
    }
}
