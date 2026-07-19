using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentPhaseExecutor.ExecuteAgentRawAsync"/>.
/// Validates that the raw execution helper correctly builds the request, executes with
/// stall monitoring, accumulates tokens, and does NOT absorb exceptions.
/// </summary>
public class ExecuteAgentRawAsyncTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;

    public ExecuteAgentRawAsyncTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _run = new PipelineRun
        {
            RunId = "test-run-1",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            WorkspacePath = "/tmp/workspace"
        };

        _config = new PipelineConfiguration
        {
            AgentTimeout = TimeSpan.FromMinutes(10),
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        // Default health status so stall monitor doesn't interfere
        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true,
                ProcessId = 1,
                IsProcessAlive = true,
                LastOutputTime = DateTime.UtcNow
            });
    }

    [Fact]
    public async Task SuccessPath_ReturnsAgentResultAndAccumulatesTokens()
    {
        // Arrange
        var usage = new TokenUsage { InputTokens = 100, OutputTokens = 50 };
        var agentResult = new AgentResult { ExitCode = 0, OutputLines = ["line1", "line2"], Usage = usage };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(agentResult);

        // Act
        var result = await AgentPhaseExecutor.ExecuteAgentRawAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            _config,
            "Test description",
            null,
            _mockLogger.Object,
            CancellationToken.None,
            phase: "test_phase");

        // Assert
        result.Should().BeSameAs(agentResult);
        _run.TotalTokens.Should().Be(150);
        // TODO: Also assert that _run.Metrics.PhaseBreakdown contains an entry for "test_phase" with expected token count to verify phase is actually recorded
    }

    [Fact]
    public async Task OperationCanceledException_Propagates()
    {
        // Arrange
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException("Cancelled"));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AgentPhaseExecutor.ExecuteAgentRawAsync(
                _mockAgent.Object,
                "test prompt",
                _run,
                _config,
                "Cancelled operation",
                null,
                _mockLogger.Object,
                CancellationToken.None));

        // ChatHistory should NOT contain any entries (exceptions are not absorbed)
        _run.ChatHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task OtherException_IsNotCaught_Propagates()
    {
        // Arrange
        var exception = new InvalidOperationException("Something went wrong");
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(exception);

        // Act & Assert
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentPhaseExecutor.ExecuteAgentRawAsync(
                _mockAgent.Object,
                "test prompt",
                _run,
                _config,
                "Failing operation",
                null,
                _mockLogger.Object,
                CancellationToken.None));

        thrown.Should().BeSameAs(exception);

        // ChatHistory should NOT contain any entries (exceptions are not absorbed)
        _run.ChatHistory.Should().BeEmpty();
    }

    [Fact]
    public async Task OnOutputLine_CalledForEachLine()
    {
        // Arrange
        var outputLines = new[] { "line1", "line2", "line3" };
        var agentResult = new AgentResult { ExitCode = 0, OutputLines = outputLines };
        var receivedLines = new List<string>();

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                foreach (var line in outputLines)
                    onLine?.Invoke(line);
            })
            .ReturnsAsync(agentResult);

        // Act
        await AgentPhaseExecutor.ExecuteAgentRawAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            _config,
            "Test description",
            null,
            _mockLogger.Object,
            CancellationToken.None,
            onOutputLine: line => receivedLines.Add(line));

        // Assert
        receivedLines.Should().BeEquivalentTo(outputLines);
    }

    [Fact]
    public async Task Request_AlwaysUsesResumeFalse()
    {
        // Arrange
        AgentRequest? capturedRequest = null;
        var agentResult = new AgentResult { ExitCode = 0, OutputLines = [] };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) => capturedRequest = req)
            .ReturnsAsync(agentResult);

        // Act
        await AgentPhaseExecutor.ExecuteAgentRawAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            _config,
            "Test description",
            null,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.UseResume.Should().BeFalse();
    }

    [Fact]
    public async Task Request_UsesConfigTimeout()
    {
        // Arrange
        AgentRequest? capturedRequest = null;
        var agentResult = new AgentResult { ExitCode = 0, OutputLines = [] };
        var customTimeout = TimeSpan.FromMinutes(42);
        var config = new PipelineConfiguration
        {
            AgentTimeout = customTimeout,
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) => capturedRequest = req)
            .ReturnsAsync(agentResult);

        // Act
        await AgentPhaseExecutor.ExecuteAgentRawAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            config,
            "Test description",
            null,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        capturedRequest.Should().NotBeNull();
        capturedRequest!.Timeout.Should().Be(customTimeout);
    }
}
