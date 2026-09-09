using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;

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
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
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
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromMilliseconds(50),
            AgentTimeout = TimeSpan.FromMinutes(30)
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
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromMilliseconds(100)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus
            {
                IsExecuting = true, ProcessId = 1, IsProcessAlive = true,
                LastOutputTime = DateTime.UtcNow.AddMinutes(-5)
            });

        var tcs = new TaskCompletionSource<AgentResult>();
        var killCalled = new TaskCompletionSource<bool>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);
        // Signal via killCalled so the test can wait until KillAsync has actually been invoked
        // before completing the task — prevents the race where tcs.SetResult unblocks the agent
        // before the monitor calls KillAsync (ChatHistory is enqueued just before KillAsync).
        _mockAgent.Setup(a => a.KillAsync())
            .Callback(() => killCalled.TrySetResult(true))
            .Returns(Task.CompletedTask);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Stuck agent", null, _mockLogger.Object, CancellationToken.None);

        // Wait for KillAsync to be invoked before completing the agent task
        await killCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));

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
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
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
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 1, IsProcessAlive = false });

        var called = false;
        var tcs = new TaskCompletionSource();
        _mockAgent.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
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

    [Fact]
    public async Task HandleSilenceWarning_EmitsStallWarningsCounter()
    {
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var warningCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.warnings");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromMilliseconds(50),
            AgentTimeout = TimeSpan.FromMinutes(30)
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
            _run, config, "Quality gate retry agent (attempt 1)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics);

        // Wait for warning to fire
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (warningCollector.GetMeasurementSnapshot().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = warningCollector.GetMeasurementSnapshot();
        snapshot.Should().NotBeEmpty("at least one stall warning should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }

    [Fact]
    public async Task HandleKillTimeoutAsync_EmitsStallKillsCounter()
    {
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var killCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.kills");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1),
            AgentTimeout = TimeSpan.FromMilliseconds(100)
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
            _run, config, "Quality gate retry agent (attempt 2)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics);

        // Wait for the kill to fire
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (killCollector.GetMeasurementSnapshot().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = killCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle("exactly one kill event should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }

    [Fact]
    public async Task HandleProcessDeath_EmitsStallProcessDeathsCounter()
    {
        var factory = new TestMeterFactory();
        var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var warningsCounter = meter.CreateCounter<long>("quality_gate.stall.warnings", "{warning}");
        var killsCounter = meter.CreateCounter<long>("quality_gate.stall.kills", "{kill}");
        var deathsCounter = meter.CreateCounter<long>("quality_gate.stall.process_deaths", "{process_death}");
        var stallMetrics = new StallMonitorMetrics(warningsCounter, killsCounter, deathsCounter);

        using var deathCollector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "quality_gate.stall.process_deaths");

        var config = new PipelineConfiguration
        {
            StallPollInterval = TimeSpan.FromMilliseconds(50),
            StallWarningInterval = TimeSpan.FromHours(1)
        };

        _mockAgent.Setup(a => a.GetHealthStatus())
            .Returns(new AgentHealthStatus { IsExecuting = true, ProcessId = 99, IsProcessAlive = false });

        var tcs = new TaskCompletionSource<AgentResult>();
        _mockAgent.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Returns(tcs.Task);

        var task = AgentStallMonitor.ExecuteWithMonitoringAsync(
            _mockAgent.Object,
            new AgentRequest { Prompt = "test", WorkspacePath = "/ws" },
            _run, config, "Quality gate retry agent (attempt 3)", null, _mockLogger.Object,
            CancellationToken.None, stallMetrics: stallMetrics);

        // Wait for process death to be detected
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (deathCollector.GetMeasurementSnapshot().Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        tcs.SetResult(new AgentResult { ExitCode = 0, OutputLines = Array.Empty<string>() });
        await task;

        var snapshot = deathCollector.GetMeasurementSnapshot();
        snapshot.Should().ContainSingle("exactly one process death event should have been emitted");
        snapshot.Should().Contain(m =>
            m.Tags.Contains(new KeyValuePair<string, object?>("phase", PipelineTelemetry.StallPhases.QgcRetryAgent)),
            "phase tag should be normalized to qgc_retry_agent");

        factory.Dispose();
    }
}
