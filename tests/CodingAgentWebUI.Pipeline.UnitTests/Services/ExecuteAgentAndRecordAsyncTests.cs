using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentPhaseExecutor.ExecuteAgentAndRecordAsync"/>.
/// Validates: Requirements 23.3–23.4
/// </summary>
public class ExecuteAgentAndRecordAsyncTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<IPipelineCallbacks> _mockCallbacks;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;
    private readonly PipelineConfiguration _config;

    public ExecuteAgentAndRecordAsyncTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockCallbacks = new Mock<IPipelineCallbacks>();
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
    public async Task SuccessPath_RecordsOutputToHistoryAndOutputLines()
    {
        // Arrange
        var outputLines = new[] { "line1", "line2", "line3" };
        var agentResult = new AgentResult { ExitCode = 0, OutputLines = outputLines };

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<AgentRequest, CancellationToken, Action<string>?>((req, ct, onLine) =>
            {
                // Simulate output line callback during execution
                foreach (var line in outputLines)
                    onLine?.Invoke(line);
            })
            .ReturnsAsync(agentResult);

        // Act
        var result = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            _config,
            "Test description",
            _mockCallbacks.Object,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.ExitCode.Should().Be(0);

        // Output lines should be enqueued to run.OutputLines via the callback
        _run.OutputLines.Should().HaveCount(3);
        _run.OutputLines.Should().Contain("line1");
        _run.OutputLines.Should().Contain("line2");
        _run.OutputLines.Should().Contain("line3");

        // ChatHistory should contain the output summary
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.Agent &&
            c.Content.Contains("line1"));

        // EmitOutputLine should have been called for each line
        _mockCallbacks.Verify(c => c.EmitOutputLine(It.IsAny<string>()), Times.Exactly(3));
    }

    [Fact]
    public async Task OperationCanceledException_IsRethrown()
    {
        // Arrange
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException("Cancelled by user"));

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                _mockAgent.Object,
                "test prompt",
                _run,
                _config,
                "Cancelled operation",
                _mockCallbacks.Object,
                _mockLogger.Object,
                CancellationToken.None));

        // ChatHistory should NOT contain an error entry (exception is rethrown, not caught)
        _run.ChatHistory.Should().NotContain(c => c.Role == ChatRole.System);
    }

    [Fact]
    public async Task OtherException_CaughtAndRecordedToChatHistory_ReturnsNull()
    {
        // Arrange
        var exception = new InvalidOperationException("Something went wrong");
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(exception);

        // Act
        var result = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
            _mockAgent.Object,
            "test prompt",
            _run,
            _config,
            "Failing operation",
            _mockCallbacks.Object,
            _mockLogger.Object,
            CancellationToken.None);

        // Assert
        result.Should().BeNull();

        // ChatHistory should contain the error message
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Failing operation") &&
            c.Content.Contains("Something went wrong"));
    }
}
