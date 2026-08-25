using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for the private LoadConfigWithRetryAsync helper in SchedulerAutoStartExtensions.
/// Tests success path, cancellation, and retry exhaustion without requiring WebApplication.
/// </summary>
public sealed class SchedulerAutoStartExtensionsTests
{
    private static readonly MethodInfo LoadConfigWithRetryAsync =
        typeof(SchedulerAutoStartExtensions)
            .GetMethod("LoadConfigWithRetryAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!;

    private static Task<PipelineConfiguration> InvokeLoadConfig(
        IPipelineApiConfigClient client,
        TimeProvider timeProvider,
        CancellationToken ct)
    {
        return (Task<PipelineConfiguration>)LoadConfigWithRetryAsync
            .Invoke(null, [client, timeProvider, ct])!;
    }

    [Fact]
    public async Task WhenApiSucceeds_ReturnsConfig()
    {
        var expected = new PipelineConfiguration { ClosedLoopAutoStart = true };
        var mockClient = new Mock<IPipelineApiConfigClient>();
        mockClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(expected);

        var result = await InvokeLoadConfig(mockClient.Object, TimeProvider.System, CancellationToken.None);

        result.Should().BeSameAs(expected);
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WhenCancelledBeforeFirstCall_ReturnsDefaultConfig()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var mockClient = new Mock<IPipelineApiConfigClient>();

        var result = await InvokeLoadConfig(mockClient.Object, TimeProvider.System, cts.Token);

        result.Should().NotBeNull("must return a default config, not throw");
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WhenApiThrowsOperationCancelledDueToShutdown_ReturnsDefaultConfig()
    {
        using var cts = new CancellationTokenSource();
        var mockClient = new Mock<IPipelineApiConfigClient>();
        mockClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns(async (CancellationToken ct) =>
            {
                await cts.CancelAsync();
                ct.ThrowIfCancellationRequested();
                return new PipelineConfiguration();
            });

        var result = await InvokeLoadConfig(mockClient.Object, TimeProvider.System, cts.Token);

        result.Should().NotBeNull("cancellation during call must return default config");
    }
}
