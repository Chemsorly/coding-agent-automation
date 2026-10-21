using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="KiroCliOrchestrator"/> computed state properties:
/// IsExecuting, ActiveProcessId, IsActiveProcessAlive, LastOutputTime.
/// These properties inspect the internal _activeProcess field. When no process
/// is running (the initial state) all properties return null/false.
/// </summary>
public class KiroCliOrchestratorPropertiesTests
{
    private readonly KiroCliOrchestrator _orchestrator;

    public KiroCliOrchestratorPropertiesTests()
    {
        var mockProcess = new Mock<IProcessWrapper>();
        mockProcess
            .Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(0);

        var config = new global::KiroCliLib.Configuration.Configuration();
        var logger = new Mock<ILogger>().Object;
        _orchestrator = new KiroCliOrchestrator(config, logger, () => mockProcess.Object);
    }

    // ── Initial state (no active process) ────────────────────────────────

    [Fact]
    public void IsExecuting_WhenNoProcessActive_ReturnsFalse()
    {
        _orchestrator.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public void ActiveProcessId_WhenNoProcessActive_ReturnsNull()
    {
        _orchestrator.ActiveProcessId.Should().BeNull();
    }

    [Fact]
    public void IsActiveProcessAlive_WhenNoProcessActive_ReturnsNull()
    {
        _orchestrator.IsActiveProcessAlive.Should().BeNull();
    }

    [Fact]
    public void LastOutputTime_WhenNoProcessActive_ReturnsNull()
    {
        _orchestrator.LastOutputTime.Should().BeNull();
    }

    // ── Properties with a non-ProcessWrapper IProcessWrapper ─────────────
    // Exercises the non-ProcessWrapper branch in ActiveProcessId
    // by using a mock IProcessWrapper (not the real ProcessWrapper class).

    [Fact]
    public async Task ActiveProcessId_WithNonProcessWrapperProvider_ReturnsNullAfterExecution()
    {
        // Mock IProcessWrapper is NOT a ProcessWrapper instance — takes the fallback path
        await _orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);

        // After execution completes, _activeProcess is cleared — should be null
        _orchestrator.ActiveProcessId.Should().BeNull();
    }

    [Fact]
    public async Task IsExecuting_AfterCompletedExecution_ReturnsFalse()
    {
        await _orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);

        _orchestrator.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public async Task LastOutputTime_AfterCompletedExecution_ReturnsNull()
    {
        await _orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);

        // Mock IProcessWrapper.LastOutputTime returns default DateTime (not tracked by mock)
        // _activeProcess is null after completion, so the property returns null
        _orchestrator.LastOutputTime.Should().BeNull();
    }

    // ── LastRunToolCalls counter tests ────────────────────────────────────────────────────────

    [Fact]
    public void LastRunToolCalls_IsEmptyBeforeFirstRun()
    {
        _orchestrator.LastRunToolCalls.Should().NotBeNull();
        _orchestrator.LastRunToolCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task LastRunToolCalls_CountsLinesFromErrorReceived()
    {
        // Two separate ErrorReceived events are required: one Running line and one status line.
        // A single stderr string would only produce one ProcessLine call and miss the attribution.
        var process = new Mock<IProcessWrapper>();
        process.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback(() =>
            {
                process.Raise(p => p.ErrorReceived += null, process.Object,
                    "[tool] Running: @sonarqube/get_issues");
                process.Raise(p => p.ErrorReceived += null, process.Object,
                    "[tool] status: Failed");
            })
            .ReturnsAsync(0);

        var config = new global::KiroCliLib.Configuration.Configuration();
        var logger = new Mock<global::Serilog.ILogger>().Object;
        var orchestrator = new KiroCliOrchestrator(config, logger, () => process.Object);

        await orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);

        orchestrator.LastRunToolCalls.Should().ContainKey("sonarqube/get_issues");
        orchestrator.LastRunToolCalls["sonarqube/get_issues"].Calls.Should().Be(1);
        orchestrator.LastRunToolCalls["sonarqube/get_issues"].Failed.Should().Be(1);
    }

    [Fact]
    public async Task LastRunToolCalls_IsResetPerExecutePromptAsync()
    {
        // First run raises a Running line — counter should have one entry.
        var processWithLine = new Mock<IProcessWrapper>();
        processWithLine.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback(() =>
            {
                processWithLine.Raise(p => p.ErrorReceived += null, processWithLine.Object,
                    "[tool] Running: @sonarqube/a");
                processWithLine.Raise(p => p.ErrorReceived += null, processWithLine.Object,
                    "[tool] status: Completed");
            })
            .ReturnsAsync(0);

        // Second run produces no ErrorReceived events.
        var processClean = new Mock<IProcessWrapper>();
        processClean.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(0);

        var queue = new Queue<IProcessWrapper>([processWithLine.Object, processClean.Object]);
        var config = new global::KiroCliLib.Configuration.Configuration();
        var logger = new Mock<global::Serilog.ILogger>().Object;
        var orchestrator = new KiroCliOrchestrator(config, logger, () => queue.Dequeue());

        await orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);
        orchestrator.LastRunToolCalls.Should().ContainKey("sonarqube/a");

        await orchestrator.ExecutePromptAsync("p", "/tmp", false, CancellationToken.None);
        orchestrator.LastRunToolCalls.Should().BeEmpty();
    }
}
