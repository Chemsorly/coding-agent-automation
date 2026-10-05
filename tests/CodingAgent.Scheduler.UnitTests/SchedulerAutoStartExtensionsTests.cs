using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Time.Testing;
using Moq;
using System.Reflection;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

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

    [Fact]
    public async Task WhenApiKeepsThrowingUntilBudgetExhausted_ReturnsDefaultConfig()
    {
        // Arrange: use FakeTimeProvider so Task.Delay calls resolve instantly.
        // A background thread continuously advances the clock so each Task.Delay
        // (up to 300s per step) resolves without real wall-clock waiting.
        var fakeTimeProvider = new FakeTimeProvider();
        var mockClient = new Mock<IPipelineApiConfigClient>();
        mockClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("simulated API unreachable"));

        // Advance the FakeTimeProvider in a background task so Task.Delay(delaySec, timeProvider, ct)
        // resolves immediately rather than blocking real wall-clock time. The short real-time delay
        // between advances lets the retry loop observe each advance before the next one.
        using var advanceCts = new CancellationTokenSource();
        var advanceTask = Task.Run(async () =>
        {
            while (!advanceCts.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(1), CancellationToken.None);
                fakeTimeProvider.Advance(TimeSpan.FromSeconds(300));
            }
        });

        var result = await InvokeLoadConfig(mockClient.Object, fakeTimeProvider, CancellationToken.None);

        await advanceCts.CancelAsync();
        await advanceTask;

        // After budget exhaustion the method must return a default config, not throw
        result.Should().NotBeNull("exhausted retry budget must return a default config, not throw");
        result.ClosedLoopAutoStart.Should().BeFalse(
            "default PipelineConfiguration must have ClosedLoopAutoStart=false so the loop does not auto-start on misconfigured startup");

        // Must have been called multiple times (retry loop actually ran)
        mockClient.Verify(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));
    }
}
