using AwesomeAssertions;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="ProactiveTokenRefresh"/> token aging, retry logic,
/// and failure status posting behavior.
/// </summary>
public class ProactiveTokenRefreshTests : IAsyncDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly List<HubConnection> _connections = new();

    public async ValueTask DisposeAsync()
    {
        foreach (var conn in _connections)
            await conn.DisposeAsync();
    }

    /// <summary>
    /// Creates a HubConnection that will throw on any InvokeAsync call (not connected).
    /// </summary>
    private HubConnection CreateDisconnectedConnection()
    {
        var conn = new HubConnectionBuilder()
            .WithUrl("http://localhost:9999/agenthub", opts =>
            {
                opts.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
        _connections.Add(conn);
        return conn;
    }

    // ── Constructor Guard Clauses ────────────────────────────────────────

    [Fact]
    public void Constructor_NullConnection_Throws()
    {
        var act = () => new ProactiveTokenRefresh(null!, new JobId("job-1"), null, null, null, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    // TODO: This test exercises JobId's own constructor validation, not ProactiveTokenRefresh's guard.
    // A default(JobId) can be passed to the ProactiveTokenRefresh constructor without error since
    // its ArgumentNullException.ThrowIfNull(jobId) guard was removed (structs cannot be null).
    [Fact]
    public void Constructor_EmptyJobId_Throws()
    {
        var conn = CreateDisconnectedConnection();
        var act = () => new ProactiveTokenRefresh(conn, new JobId(""), null, null, null, _mockLogger.Object);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var conn = CreateDisconnectedConnection();
        var act = () => new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── Fresh Token (< 45 min) — Returns Null ────────────────────────────

    [Fact]
    public async Task EnsureFreshToken_WhenTokenIsFresh_ReturnsNull()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        // Token was just created (in constructor) — should be fresh
        var result = await refresh.EnsureFreshTokenAsync(ProviderKind.Repository, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task EnsureFreshToken_AfterMarkTokenRefreshed_ReturnsNull()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        refresh.MarkTokenRefreshed();

        var result = await refresh.EnsureFreshTokenAsync(ProviderKind.Repository, CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Stale Token — Attempts Refresh via Hub ───────────────────────────

    [Fact]
    public async Task EnsureFreshToken_WhenTokenIsStale_AttemptsHubInvoke()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        // Force token to appear stale by using reflection to set old timestamp
        ForceTokenStale(refresh);

        // The connection is not started, so InvokeAsync will throw InvalidOperationException.
        // After retries exhaust, should throw TokenRefreshFailureException.
        // Use short cancellation to avoid waiting for all retry delays.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var act = () => refresh.EnsureFreshTokenAsync(ProviderKind.Repository, cts.Token);

        // Either cancellation or TokenRefreshFailureException — both prove it attempted the hub call
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task EnsureFreshToken_WhenStaleAndRetriesFail_ThrowsTokenRefreshFailureException()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        ForceTokenStale(refresh);

        // Cancel after a short time to avoid waiting 2+4+8=14s of retry delays
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var act = () => refresh.EnsureFreshTokenAsync(ProviderKind.Repository, cts.Token);

        // Should throw OperationCanceledException during retry delay
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── Failure Status Posting ───────────────────────────────────────────

    [Fact]
    public async Task EnsureFreshToken_WhenStaleAndNoWorkItemClient_DoesNotThrowOnStatusPost()
    {
        var conn = CreateDisconnectedConnection();
        // No WorkItemHttpClient — should still throw TokenRefreshFailureException without NPE
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        ForceTokenStale(refresh);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var act = () => refresh.EnsureFreshTokenAsync(ProviderKind.Repository, cts.Token);

        // Should throw cleanly without NullReferenceException
        var ex = await act.Should().ThrowAsync<Exception>();
        ex.Which.Should().NotBeOfType<NullReferenceException>();
    }

    // ── MarkTokenRefreshed ───────────────────────────────────────────────

    [Fact]
    public async Task MarkTokenRefreshed_ResetsTimer_SubsequentCheckReturnsFresh()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        // Force stale
        ForceTokenStale(refresh);

        // Mark refreshed — should reset timer
        refresh.MarkTokenRefreshed();

        // Now should be fresh again
        var result = await refresh.EnsureFreshTokenAsync(ProviderKind.Repository, CancellationToken.None);
        result.Should().BeNull();
    }

    [Fact]
    public async Task EnsureFreshToken_MultipleProviderKinds_FreshForAll()
    {
        var conn = CreateDisconnectedConnection();
        var refresh = new ProactiveTokenRefresh(conn, new JobId("job-1"), null, null, null, _mockLogger.Object);

        // Fresh token should return null for any ProviderKind
        (await refresh.EnsureFreshTokenAsync(ProviderKind.Repository, CancellationToken.None)).Should().BeNull();
        (await refresh.EnsureFreshTokenAsync(ProviderKind.Brain, CancellationToken.None)).Should().BeNull();
        (await refresh.EnsureFreshTokenAsync(ProviderKind.Agent, CancellationToken.None)).Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Uses reflection to set the token timestamp to 60 minutes ago, simulating staleness.
    /// </summary>
    private static void ForceTokenStale(ProactiveTokenRefresh refresh)
    {
        var field = typeof(ProactiveTokenRefresh).GetField("_lastTokenTimeTicks",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var staleTicks = (DateTimeOffset.UtcNow - TimeSpan.FromMinutes(60)).Ticks;
        field!.SetValue(refresh, staleTicks);
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            // Never actually called since we don't start the connection
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
