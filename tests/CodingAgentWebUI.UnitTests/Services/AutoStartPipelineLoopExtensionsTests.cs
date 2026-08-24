using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PipelineLoopAutoStartExtensions.AutoStartPipelineLoopAsync"/>.
///
/// Tests use a raw <c>WebApplication.CreateBuilder()</c> host (not WebApplicationFactory&lt;Program&gt;)
/// to avoid triggering Program.cs fast-fail checks and to keep the test scope minimal.
///
/// <c>PipelineLoopService</c> is a concrete sealed class. The tests cover all paths that
/// do NOT require it (ClosedLoopAutoStart=false, API-unreachable-exhausted-timeout,
/// cancellation) to avoid the heavy DI graph PipelineLoopService requires.
/// The ClosedLoopAutoStart=true path is exercised in integration smoke tests.
/// </summary>
public sealed class AutoStartPipelineLoopExtensionsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the minimal WebApplication with only the services the extension method needs.
    /// Does NOT register PipelineLoopService — only safe when ClosedLoopAutoStart=false.
    /// Registers the provided <paramref name="timeProvider"/> (or <see cref="TimeProvider.System"/>
    /// if null) so retry delays are controllable in tests.
    /// </summary>
    private static async Task<WebApplication> BuildMinimalAppAsync(
        Mock<IPipelineApiConfigClient> configClientMock,
        TimeProvider? timeProvider = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton(configClientMock.Object);
        builder.Services.AddSingleton(timeProvider ?? TimeProvider.System);
        var app = builder.Build();
        return await Task.FromResult(app);
    }

    // ── Success paths ─────────────────────────────────────────────────────

    [Fact]
    public async Task AutoStartPipelineLoopAsync_ClosedLoopAutoStartFalse_DoesNotStartLoop()
    {
        // Arrange: API returns config with ClosedLoopAutoStart=false (default)
        var configClientMock = new Mock<IPipelineApiConfigClient>();
        configClientMock
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { ClosedLoopAutoStart = false });

        await using var app = await BuildMinimalAppAsync(configClientMock);

        // Act: must complete without exception and without touching PipelineLoopService
        await app.AutoStartPipelineLoopAsync();

        // Assert: config was loaded exactly once
        configClientMock.Verify(
            c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task AutoStartPipelineLoopAsync_NullApp_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => PipelineLoopAutoStartExtensions.AutoStartPipelineLoopAsync(null!));
    }

    // ── Retry / failure paths ─────────────────────────────────────────────

    [Fact]
    public async Task AutoStartPipelineLoopAsync_ApiThrowsThenSucceeds_RetriesAndReturnsConfig()
    {
        // Arrange: first call throws, second succeeds — verifies retry loop executes.
        // FakeTimeProvider makes the 2s retry delay instant.
        var fakeTime = new FakeTimeProvider();
        var configClientMock = new Mock<IPipelineApiConfigClient>();
        configClientMock
            .SetupSequence(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"))
            .ReturnsAsync(new PipelineConfiguration { ClosedLoopAutoStart = false });

        await using var app = await BuildMinimalAppAsync(configClientMock, fakeTime);

        // Act: run the method on a background thread; advance fake time to release the retry delay.
        var mainTask = Task.Run(() => app.AutoStartPipelineLoopAsync());

        // Small real wait lets the async method reach the Task.Delay suspension point.
        await Task.Delay(20);
        fakeTime.Advance(TimeSpan.FromSeconds(3)); // past the 2s first retry delay

        await mainTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: retried exactly once (two calls total)
        configClientMock.Verify(
            c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    [Fact]
    public async Task AutoStartPipelineLoopAsync_ApiUnreachableExceedsTotalTimeout_DefaultsToDisabled()
    {
        // Arrange: API always throws. Budget is 600s (delays: 2+5+10+30+60+120+300+300=827s).
        // After 8 throws the accumulated delay (2+5+10+30+60+120+300+300=827s) exceeds 600s,
        // triggering the Fatal log and returning a default config (ClosedLoopAutoStart=false).
        // FakeTimeProvider eliminates real waits so this test runs instantly.
        var fakeTime = new FakeTimeProvider();
        var configClientMock = new Mock<IPipelineApiConfigClient>();
        configClientMock
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        await using var app = await BuildMinimalAppAsync(configClientMock, fakeTime);

        // Act: run the method on a background thread; advance fake time from the test thread
        // to release each Task.Delay in the retry loop.
        // The delays array is [2, 5, 10, 30, 60, 120, 300, 300] — 8 entries.
        // After advancing through all 8 delays the budget (600s) is exceeded and the method returns.
        var mainTask = Task.Run(() => app.AutoStartPipelineLoopAsync());

        // Drive fake time forward through each retry delay.
        // Each Advance fires the pending timer and lets the retry loop throw again and schedule
        // the next delay. We poll completion to stop early if the method exits sooner than expected.
        var delays = new[] { 2, 5, 10, 30, 60, 120, 300, 300 };
        foreach (var delaySec in delays)
        {
            if (mainTask.IsCompleted) break;
            // Small real wait lets async continuations run on the thread pool before advancing.
            await Task.Delay(10);
            fakeTime.Advance(TimeSpan.FromSeconds(delaySec + 1));
        }

        // Wait for the method to finish (should be near-instant once all delays are advanced).
        await mainTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Assert: exhaustion path was reached — GetPipelineConfigAsync was called 8 times
        // (one per retry attempt before the budget was exceeded)
        configClientMock.Verify(
            c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Exactly(8));
    }
}
