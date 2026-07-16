using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for QualityGateExecutor retry loop mechanics:
/// retry exhaustion, cancellation between iterations, mixed gate results, and first-attempt success.
/// </summary>
public class QualityGateExecutorRetryTests
{
    private readonly Mock<IQualityGateValidator> _mockValidator;
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<IRepositoryProvider> _mockRepoProvider;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly QualityGateExecutor _executor;

    private static readonly QualityGateReport FailingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error CS1234" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "2 tests failed" }
    };

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly QualityGateReport CompilationFailsTestsPass = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false, Details = "Build error" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "All tests passed" }
    };

    private static readonly QualityGateReport CompilationPassesTestsFail = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Build succeeded" },
        Tests = new GateResult { GateName = "Tests", Passed = false, Details = "3 tests failed" }
    };

    public QualityGateExecutorRetryTests()
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
            RunId = "test-run-retry",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"qg-retry-test-{Guid.NewGuid():N}")
        };

        _executor = new QualityGateExecutor(
            _mockValidator.Object,
            new PullRequestOrchestrator(_mockLogger.Object),
            new CiLogWriter(_mockLogger.Object),
            new FeedbackService(_mockLogger.Object),
            _mockLogger.Object,
            _mockHistoryService.Object);

        // Default callback setups (follow QualityGateExecutorFeedbackTests pattern)
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

        // Default issue ops
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Default history service
        _mockHistoryService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

        // Default agent health status (IsProcessAlive defaults to null — safe for stall monitor)
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = false });

        // Default agent execution — returns non-empty output to avoid empty-response detection
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed the issue" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });
    }

    // ── Retry Exhaustion ─────────────────────────────────────────────────────

    [Fact]
    public async Task RetryExhaustion_FinalizesPullRequestAsDraft()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RetryExhaustion_IncrementsRetryCountCorrectly()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _run.RetryCount.Should().Be(2);
    }

    [Fact]
    public async Task RetryExhaustion_PopulatesRetryErrorsOnEachFailure()
    {
        var config = CreateConfig(maxRetries: 2);
        SetupValidatorAlwaysFails();

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Initial failure + 2 retry failures = 3 error entries
        // (initial failure error is added in the retry loop on first iteration,
        //  plus the final exhaustion error added after the loop)
        _run.RetryErrors.Should().NotBeEmpty();
        _run.RetryErrors.Count.Should().BeGreaterThanOrEqualTo(2);
    }

    // ── Cancellation Between Retry Iterations ────────────────────────────────

    [Fact]
    public async Task Cancellation_BetweenRetries_TransitionsToCancelled()
    {
        var config = CreateConfig(maxRetries: 3);
        var callCount = 0;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns((string _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken _, string? _) =>
            {
                callCount++;
                if (callCount >= 2)
                    throw new OperationCanceledException();
                return Task.FromResult(FailingReport);
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.Cancelled), Times.Once);
    }

    [Fact]
    public async Task Cancellation_BetweenRetries_SwapsToAgentCancelledLabel()
    {
        var config = CreateConfig(maxRetries: 3);
        var callCount = 0;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .Returns((string _, IReadOnlyList<QualityGateConfiguration> _, CancellationToken _, string? _) =>
            {
                callCount++;
                if (callCount >= 2)
                    throw new OperationCanceledException();
                return Task.FromResult(FailingReport);
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _mockCallbacks.Verify(
            c => c.SwapAgentLabel(_run.IssueIdentifier, AgentLabels.Cancelled, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Mixed Gate Results — Retry Prompt Content ────────────────────────────

    [Fact]
    public async Task MixedResults_CompilationPassesTestsFail_PromptContainsBothStatuses()
    {
        var config = CreateConfig(maxRetries: 1);
        string? capturedPrompt = null;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(CompilationPassesTestsFail);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    capturedPrompt = req.Prompt;
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("Compilation: PASSED");
        capturedPrompt.Should().Contain("Tests: FAILED");
    }

    [Fact]
    public async Task MixedResults_TestsPassCompilationFails_PromptContainsBothStatuses()
    {
        var config = CreateConfig(maxRetries: 1);
        string? capturedPrompt = null;

        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(CompilationFailsTestsPass);

        _mockAgent.Setup(a => a.ExecuteAsync(
                It.IsAny<AgentRequest>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) =>
            {
                if (!req.Prompt.Contains("Pipeline Failure Feedback"))
                    capturedPrompt = req.Prompt;
            })
            .ReturnsAsync(new AgentResult
            {
                ExitCode = 0,
                OutputLines = new[] { "Fixed" },
                Usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 }
            });

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        capturedPrompt.Should().NotBeNull();
        capturedPrompt.Should().Contain("Compilation: FAILED");
        capturedPrompt.Should().Contain("Tests: PASSED");
    }

    // ── First-Attempt Success ────────────────────────────────────────────────

    [Fact]
    public async Task FirstAttemptSuccess_NoRetry_FinalizesAsNonDraft()
    {
        var config = CreateConfig(maxRetries: 3);
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        _run.RetryCount.Should().Be(0);
        _mockCallbacks.Verify(c => c.FinalizePullRequest(
            _run, It.IsAny<QualityGateReport>(), false, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task FirstAttemptSuccess_NoAgentFixCallMade()
    {
        var config = CreateConfig(maxRetries: 3);
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(PassingReport);

        await _executor.ProceedToQualityGatesAsync(BuildContext(config), CancellationToken.None);

        // Agent is called for cleanup but NOT for retry fixes or feedback
        // Verify no call with "Quality gates failed" prompt (retry prompt signature)
        _mockAgent.Verify(a => a.ExecuteAsync(
            It.Is<AgentRequest>(r => r.Prompt.Contains("Quality gates failed")),
            It.IsAny<CancellationToken>(),
            It.IsAny<Action<string>?>()), Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static PipelineConfiguration CreateConfig(int maxRetries) => new()
    {
        AgentTimeout = TimeSpan.FromMinutes(10),
        MaxRetries = maxRetries,
        StallPollInterval = TimeSpan.FromMilliseconds(50),
        StallWarningInterval = TimeSpan.FromHours(1)
    };

    private void SetupValidatorAlwaysFails()
    {
        _mockValidator.Setup(v => v.ValidateAsync(
                It.IsAny<string>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<string?>()))
            .ReturnsAsync(FailingReport);
    }

    private QualityGateContext BuildContext(PipelineConfiguration config) => new()
    {
        Run = _run,
        Config = config,
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
            Identifier = "99",
            Title = "Test Issue",
            Description = "Test issue description",
            Labels = new[] { "bug" }
        }
    };
}
