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
}
