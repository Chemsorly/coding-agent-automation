using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Validates that consumers can depend on <see cref="IPipelineOrchestrationService"/> for cancel
/// interactions rather than coupling to the concrete <see cref="Pipeline.Services.PipelineOrchestrationService"/>.
/// These tests use <c>Mock&lt;IPipelineOrchestrationService&gt;</c> — never the concrete class —
/// demonstrating the injection pattern the interface enables.
/// </summary>
public class IPipelineOrchestrationServiceContractTests
{
    // ── Mock<IPipelineOrchestrationService> consumer pattern ─────────────────

    /// <summary>
    /// A consumer that holds <see cref="IPipelineOrchestrationService"/> by interface and
    /// delegates cancellation to it. Represents any future class that triggers pipeline
    /// cancellation without needing the full concrete service.
    /// </summary>
    private sealed class CancelTrigger(IPipelineOrchestrationService orchestration)
    {
        public Task TriggerCancelAsync() => orchestration.CancelPipelineAsync();
    }

    [Fact]
    public async Task Consumer_InvokesCancelPipelineAsync_WhenCancelTriggered()
    {
        // Arrange
        var mock = new Mock<IPipelineOrchestrationService>();
        mock.Setup(s => s.CancelPipelineAsync()).Returns(Task.CompletedTask);

        var consumer = new CancelTrigger(mock.Object);

        // Act
        await consumer.TriggerCancelAsync();

        // Assert — consumer correctly delegated to the interface
        mock.Verify(s => s.CancelPipelineAsync(), Times.Once);
    }

    [Fact]
    public async Task Consumer_DoesNotCallCancelPipelineAsync_WhenCancelNotTriggered()
    {
        // Arrange
        var mock = new Mock<IPipelineOrchestrationService>();
        var _ = new CancelTrigger(mock.Object);

        // Act — consumer created but TriggerCancelAsync never called

        // Assert — no spurious cancel call
        await Task.CompletedTask;
        mock.Verify(s => s.CancelPipelineAsync(), Times.Never);
    }

    // ── TestOrchestrationFactory.CreateMinimalInterface ──────────────────────

    [Fact]
    public void CreateMinimalInterface_WithNoArg_ReturnsNoOpImplementation()
    {
        var service = TestOrchestrationFactory.CreateMinimalInterface();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task CreateMinimalInterface_NoOp_CancelPipelineAsync_CompletesWithoutThrowing()
    {
        var service = TestOrchestrationFactory.CreateMinimalInterface();

        // No-op must complete silently
        await service.CancelPipelineAsync();
    }

    [Fact]
    public void CreateMinimalInterface_WithMock_ReturnsSameInstance()
    {
        var mock = new Mock<IPipelineOrchestrationService>();

        var service = TestOrchestrationFactory.CreateMinimalInterface(mock.Object);

        Assert.Same(mock.Object, service);
    }
}
