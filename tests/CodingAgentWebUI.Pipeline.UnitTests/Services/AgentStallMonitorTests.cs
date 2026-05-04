using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentStallMonitor"/>.
/// </summary>
public class AgentStallMonitorTests
{
    private readonly Mock<IAgentProvider> _mockAgent;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly PipelineRun _run;

    public AgentStallMonitorTests()
    {
        _mockAgent = new Mock<IAgentProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        };
    }

    [Fact]
    public async Task DetectsProcessDeath_LogsErrorWithPhaseContext()
    {
        var config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration { StallPollInterval = TimeSpan.FromMilliseconds(50), StallWarningInterval = TimeSpan.FromHours(1) }
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 42, IsProcessAlive = false });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Test phase", null, _mockLogger.Object, CancellationToken.None);

        // Wait for the monitor to detect the dead process before completing the agent call
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Test phase") &&
            c.Content.Contains("agent process is no longer alive") &&
            c.Content.Contains("42"));
    }

    [Fact]
    public async Task DetectsSilence_LogsWarningWithPhaseContext()
    {
        var config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration { StallPollInterval = TimeSpan.FromMilliseconds(50), StallWarningInterval = TimeSpan.FromMilliseconds(50), AgentTimeout = TimeSpan.FromMinutes(30) }
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true, ProcessId = 1, IsProcessAlive = true,
                LastOutputTime = DateTime.UtcNow.AddMinutes(-3)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Code review agent 'Correctness'", null, _mockLogger.Object, CancellationToken.None);

        // Wait for the monitor to log the silence warning before completing the agent call
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Code review agent 'Correctness'") &&
            c.Content.Contains("no output for"));
    }

    [Fact]
    public async Task KillsAfterHardTimeout_CallsKillAsync()
    {
        var config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration { StallPollInterval = TimeSpan.FromMilliseconds(50), StallWarningInterval = TimeSpan.FromHours(1), AgentTimeout = TimeSpan.FromMilliseconds(100) }
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true, ProcessId = 1, IsProcessAlive = true,
                LastOutputTime = DateTime.UtcNow.AddMinutes(-5)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);
        _mockAgent.Setup(a => a.KillAsync()).Returns(Task.CompletedTask);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Stuck agent", null, _mockLogger.Object, CancellationToken.None);

        // Wait for the monitor to kill the agent before completing the agent call
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        _mockAgent.Verify(a => a.KillAsync(), Times.Once);
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Forcefully terminating agent process"));
    }

    [Fact]
    public async Task CancelsCleanlyOnNormalCompletion()
    {
        var config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration { StallPollInterval = TimeSpan.FromMilliseconds(50), StallWarningInterval = TimeSpan.FromHours(1) }
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = true, LastOutputTime = DateTime.UtcNow });

        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });

        var result = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Fast agent", null, _mockLogger.Object, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        _mockAgent.Verify(a => a.KillAsync(), Times.Never);
    }

    [Fact]
    public async Task MonitorAsync_WrapsVoidAgentCall()
    {
        var config = new PipelineConfiguration
        {
            Retry = new RetryConfiguration { StallPollInterval = TimeSpan.FromMilliseconds(50), StallWarningInterval = TimeSpan.FromHours(1) }
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = false });

        var called = false;
        var tcs = new TaskCompletionSource();
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(() => { called = true; return tcs.Task; });

        var task = AgentStallMonitor.MonitorAsync(
            _mockAgent.Object,
            () => _mockAgent.Object.EnsureSessionAsync("/ws", CancellationToken.None),
            _run, config, "Session warm-up", null, _mockLogger.Object, CancellationToken.None);

        // Wait for the monitor to detect the dead process before completing the agent call
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_run.ChatHistory.IsEmpty && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult();
        await task;

        called.Should().BeTrue();
        _run.ChatHistory.Should().Contain(c =>
            c.Role == ChatRole.System &&
            c.Content.Contains("Session warm-up") &&
            c.Content.Contains("agent process is no longer alive"));
    }
}
