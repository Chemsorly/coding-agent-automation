using AwesomeAssertions;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="NoOpKiroCliOrchestrator"/>.
/// </summary>
public class NoOpKiroCliOrchestratorTests
{
    private readonly NoOpKiroCliOrchestrator _orchestrator = new();

    [Fact]
    public void IsExecuting_AlwaysFalse()
    {
        _orchestrator.IsExecuting.Should().BeFalse();
    }

    [Fact]
    public void ActiveProcessId_AlwaysNull()
    {
        _orchestrator.ActiveProcessId.Should().BeNull();
    }

    [Fact]
    public void IsActiveProcessAlive_AlwaysNull()
    {
        _orchestrator.IsActiveProcessAlive.Should().BeNull();
    }

    [Fact]
    public void LastOutputTime_AlwaysNull()
    {
        _orchestrator.LastOutputTime.Should().BeNull();
    }

    [Fact]
    public async Task ExecutePromptAsync_ThrowsNotSupportedException()
    {
        var act = () => _orchestrator.ExecutePromptAsync(
            "test prompt", "/workspace", false, CancellationToken.None);
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public async Task ExecutePromptAsync_WithCallback_ThrowsNotSupportedException()
    {
        var act = () => _orchestrator.ExecutePromptAsync(
            "test prompt", "/workspace", true, CancellationToken.None, _ => { });
        await act.Should().ThrowAsync<NotSupportedException>();
    }

    [Fact]
    public void Kill_DoesNotThrow()
    {
        var act = () => _orchestrator.Kill();
        act.Should().NotThrow();
    }
}
