using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="InfiniteRetryPolicy"/> inside <see cref="AgentHubConnection"/>.
///
/// The policy drives all SignalR reconnect behaviour for the monolith UI. A null return stops
/// retrying permanently; an uncapped back-off leaves the UI dark for minutes.
/// </summary>
public sealed class AgentHubConnectionRetryPolicyTests
{
    private static RetryContext Context(long retryCount) =>
        new() { PreviousRetryCount = retryCount, RetryReason = null, ElapsedTime = TimeSpan.Zero };

    // ── NeverReturnsNull ────────────────────────────────────────────────────

    /// <summary>
    /// Returning null from <c>NextRetryDelay</c> signals SignalR to stop retrying permanently.
    /// <see cref="InfiniteRetryPolicy"/> must never return null for any retry count.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(50)]
    [InlineData(1_000_000)]
    public void NextRetryDelay_NeverReturnsNull(long retryCount)
    {
        var policy = new InfiniteRetryPolicy();

        var delay = policy.NextRetryDelay(Context(retryCount));

        delay.Should().NotBeNull(because: "null stops all retries and leaves the UI permanently disconnected");
    }

    // ── Backoff cap ─────────────────────────────────────────────────────────

    /// <summary>
    /// Base delay is capped at 2^7 = 128 s. With up to 1 000 ms jitter the maximum total
    /// delay is ~129 s. We sample 20 times to cover the full jitter range.
    /// </summary>
    [Theory]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(20)]
    [InlineData(100)]
    public void NextRetryDelay_HighRetryCount_CapsBelow130Seconds(long retryCount)
    {
        var policy = new InfiniteRetryPolicy();
        var maxAllowed = TimeSpan.FromSeconds(130); // 128 s base + 1 s jitter ceiling

        for (var i = 0; i < 20; i++)
        {
            var delay = policy.NextRetryDelay(Context(retryCount));

            delay.Should().BeLessThan(maxAllowed,
                because: $"backoff must cap to avoid leaving UI disconnected for minutes (retry #{retryCount}, sample {i})");
        }
    }

    // ── Backoff increases with retry count ──────────────────────────────────

    /// <summary>
    /// The first 8 distinct retry counts (0–7) should produce exponentially increasing median
    /// delays. We sample many times to smooth out the 0–1 s jitter.
    /// </summary>
    [Fact]
    public void NextRetryDelay_EarlyRetries_MedianDelayIncreasesWithRetryCount()
    {
        var policy = new InfiniteRetryPolicy();
        const int samples = 200;

        double[] medians = new double[8];
        for (var rc = 0; rc <= 7; rc++)
        {
            var delays = Enumerable.Range(0, samples)
                .Select(_ => policy.NextRetryDelay(Context(rc))!.Value.TotalSeconds)
                .OrderBy(x => x)
                .ToArray();
            medians[rc] = delays[samples / 2];
        }

        for (var i = 1; i <= 7; i++)
        {
            medians[i].Should().BeGreaterThan(medians[i - 1],
                because: $"median back-off at retry {i} (≈{medians[i]:F1}s) should exceed retry {i - 1} (≈{medians[i - 1]:F1}s)");
        }
    }

    // ── DisposeAsync ────────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="AgentHubConnection.DisposeAsync"/> calls StopAsync before DisposeAsync on the
    /// inner <see cref="HubConnection"/>. Reversing the order causes ObjectDisposedException.
    /// Test exercises the dispose path without ever connecting.
    /// </summary>
    [Fact]
    public async Task AgentHubConnection_DisposeAsync_DoesNotThrow()
    {
        var conn = new AgentHubConnection("http://localhost:59999/hubs/agent-test", "test-api-key");

        var act = () => conn.DisposeAsync().AsTask();

        await act.Should().NotThrowAsync(
            because: "DisposeAsync must call StopAsync before DisposeAsync on the inner connection");
    }
}
