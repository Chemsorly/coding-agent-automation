using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for PipelineOrchestrationService disposal and idle-state lifecycle guards.
/// Validates:
/// - CancelPipelineAsync when no run is active is a safe no-op
/// - DisposeAsync called twice does not throw
/// - Sync Dispose followed by DisposeAsync is idempotent
/// </summary>
public class PipelineOrchestrationServiceLifecycleTests
{
    private static PipelineOrchestrationService CreateService(PipelineRunLifecycleService? lifecycle = null)
    {
        var configStore = new Mock<IConfigurationStore>();
        var providerFactory = new Mock<IProviderFactory>();

        return TestOrchestrationFactory.CreateMinimal(
            configStore: configStore.Object,
            providerFactory: providerFactory.Object,
            lifecycle: lifecycle);
    }

    // ── CancelPipelineAsync: no active run ────────────────────────────

    [Fact]
    public async Task CancelPipelineAsync_WhenNotRunning_ReturnsWithoutAction()
    {
        // Arrange — service created with no active run (lifecycle.ActiveRun == null)
        await using var service = CreateService();

        // Act — must complete without throwing or attempting label swap
        var ex = await Record.ExceptionAsync(() => service.CancelPipelineAsync());

        // Assert
        ex.Should().BeNull("idle cancel must be a safe no-op");
    }

    // ── DisposeAsync idempotency ──────────────────────────────────────

    [Fact]
    public async Task DisposeAsync_CalledTwice_IsIdempotent()
    {
        var service = CreateService();

        // First dispose — normal path
        await service.DisposeAsync();

        // Second dispose — must hit the _disposed guard and return silently
        var ex = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());

        ex.Should().BeNull("second DisposeAsync must not throw");
    }

    [Fact]
    public async Task Dispose_ThenDisposeAsync_IsIdempotent()
    {
        var service = CreateService();

        // Sync dispose first
        service.Dispose();

        // Async dispose on already-disposed instance — _disposed is true, should return immediately
        var ex = await Record.ExceptionAsync(() => service.DisposeAsync().AsTask());

        ex.Should().BeNull("DisposeAsync after Dispose must not throw");
    }
}
