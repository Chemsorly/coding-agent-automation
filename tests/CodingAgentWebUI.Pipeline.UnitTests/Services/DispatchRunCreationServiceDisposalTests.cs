using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="DispatchRunCreationService"/> correctly implements
/// <see cref="IAsyncDisposable"/> and delegates disposal to the owned
/// <see cref="PipelineProviderManager"/>.
/// </summary>
public class DispatchRunCreationServiceDisposalTests
{
    private static DispatchRunCreationService CreateService()
    {
        var mockConfigStore = new Mock<IProviderConfigStore>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockHistoryService = new Mock<IPipelineRunHistoryService>();

        var lifecycle = new PipelineRunLifecycleService(
            mockHistoryService.Object, null, mockLogger.Object);

        return new DispatchRunCreationService(
            lifecycle,
            mockConfigStore.Object,
            mockFactory.Object,
            mockLogger.Object);
    }

    [Fact]
    public void Implements_IAsyncDisposable()
    {
        typeof(IAsyncDisposable).IsAssignableFrom(typeof(DispatchRunCreationService))
            .Should().BeTrue();
    }

    [Fact]
    public void Implements_IDisposable()
    {
        typeof(IDisposable).IsAssignableFrom(typeof(DispatchRunCreationService))
            .Should().BeTrue();
    }

    [Fact]
    // TODO: This test does not verify that disposal is actually delegated to the owned _providerManager.
    // It passes even if DisposeAsync() body is empty because no active providers are set up.
    // Consider setting up PipelineProviderManager with an active provider or using a mock/spy to confirm delegation.
    public async Task DisposeAsync_DoesNotThrow()
    {
        var service = CreateService();

        var act = () => service.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    // TODO: This test only exercises the "no active providers" path and doesn't prove idempotency
    // under real disposal conditions. Consider testing with previously-active providers to verify
    // that double-dispose is safe when providers have been resolved and cached.
    public async Task DisposeAsync_IsIdempotent()
    {
        var service = CreateService();

        // First disposal
        await service.DisposeAsync();

        // Second disposal should not throw
        var act = () => service.DisposeAsync().AsTask();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var service = CreateService();

        var act = () => service.Dispose();

        act.Should().NotThrow();
    }
}
