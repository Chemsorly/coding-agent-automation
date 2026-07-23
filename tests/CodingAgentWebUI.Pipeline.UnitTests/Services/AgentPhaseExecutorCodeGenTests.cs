using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Isolated unit tests for <see cref="AgentPhaseExecutor.ExecuteCodeGenerationAsync"/>.
/// Tests success path, timeout, exception handling, prompt override, and session capture.
/// </summary>
public class AgentPhaseExecutorCodeGenTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<IAgentIssueOperations> _mockIssueOps;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;
    private readonly AgentPhaseExecutor _executor;

    public AgentPhaseExecutorCodeGenTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
        _mockIssueOps = new Mock<IAgentIssueOperations>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-codegen",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = "/tmp/workspace-codegen"
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _executor = new AgentPhaseExecutor(_mockLogger.Object);

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });
        _mockIssueOps.Setup(o => o.SwapLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task CodeGen_SuccessExitCode_ReturnsTrueAndTransitions()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = ExitCodes.Success, OutputLines = new[] { "Done" } });

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeTrue();
        _mockCallbacks.Verify(c => c.TransitionTo(PipelineStep.GeneratingCode), Times.Once);
    }

    [Fact]
    public async Task CodeGen_NonZeroNonTimeoutExitCode_ContinuesToQualityGates()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = ExitCodes.GeneralFailure, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CodeGen_TimeoutExitCode_FailsPhase()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = ExitCodes.Timeout, OutputLines = Array.Empty<string>() });

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("timed out");
    }

    [Fact]
    public async Task CodeGen_OperationCancelledWithoutOrchestratorCts_FailsPhase()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException());

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("timed out");
    }

    [Fact]
    public async Task CodeGen_OperationCancelledWithOrchestratorCts_Rethrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException());

        var context = BuildContext(orchestratorCts: cts);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _executor.ExecuteCodeGenerationAsync(context, CancellationToken.None));
    }

    [Fact]
    public async Task CodeGen_ExceptionWithNoFileChanges_FailsPhase()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new IOException("disk full"));

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeFalse();
        _run.FailureReason.Should().Contain("no file changes");
    }

    [Fact]
    public async Task CodeGen_ExceptionWithFileChanges_ContinuesToQualityGates()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new IOException("connection reset"));
        _mockCallbacks.Setup(c => c.UpdateFileChangeStats(It.IsAny<PipelineRun>()))
            .Callback<PipelineRun>(r => r.FilesChangedCount = 3)
            .Returns(Task.CompletedTask);

        var result = await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task CodeGen_PromptOverride_UsedInsteadOfDefault()
    {
        string? capturedPrompt = null;
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, _, _) => capturedPrompt = req.Prompt)
            .ReturnsAsync(new AgentResult { ExitCode = ExitCodes.Success, OutputLines = Array.Empty<string>() });

        await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None, promptOverride: "custom prompt");

        capturedPrompt.Should().Be("custom prompt");
    }

    [Fact]
    public async Task CodeGen_SessionIdCaptured_FromProvider()
    {
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = ExitCodes.Success, OutputLines = Array.Empty<string>() });
        _mockAgent.Setup(a => a.GetLatestSessionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("session-abc");

        await _executor.ExecuteCodeGenerationAsync(BuildContext(), CancellationToken.None);

        _run.CodegenSessionId.Should().Be("session-abc");
    }

    private AgentPhaseContext BuildContext(CancellationTokenSource? orchestratorCts = null)
    {
        return new AgentPhaseContext
        {
            Run = _run,
            Config = _config,
            AgentProvider = _mockAgent.Object,
            IssueOps = _mockIssueOps.Object,
            Callbacks = _mockCallbacks.Object,
            OrchestratorCts = orchestratorCts,
            Issue = new IssueDetail { Identifier = "42", Title = "Test Issue", Description = "Test description", Labels = new[] { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "Test requirements", AcceptanceCriteria = new[] { "AC1", "AC2" } }
        };
    }
}
