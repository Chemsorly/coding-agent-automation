using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="OrchestratorProxy.RequestTokenRefreshAsync"/> single-flight pattern.
/// Verifies:
/// - Concurrent same-kind callers invoke the delegate only once.
/// - A faulted delegate is not cached and is re-invoked on the next call.
/// - Different ProviderKinds issue independent requests (no cross-kind sharing).
/// - Successful responses are cached for the token lifetime.
/// - OrchestratorProxy is IDisposable and disposes the semaphore.
/// </summary>
public class OrchestratorProxySingleFlightTests
{
    private static readonly DateTimeOffset FutureExpiry = DateTimeOffset.UtcNow.AddHours(1);
    private static readonly DateTimeOffset NearExpiry = DateTimeOffset.UtcNow.AddSeconds(1); // within renewal buffer

    private static HubConnection BuildDummyConnection() =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost:9999/hubs/agent")
            .Build();

    // ── IDisposable ────────────────────────────────────────────────────────

    [Fact]
    public void OrchestratorProxy_IsIDisposable()
    {
        var proxy = new OrchestratorProxy(BuildDummyConnection(), "job-1");
        proxy.Should().BeAssignableTo<IDisposable>();
    }

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var proxy = new OrchestratorProxy(BuildDummyConnection(), "job-1");
        var act = () => proxy.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_Idempotent()
    {
        var proxy = new OrchestratorProxy(BuildDummyConnection(), "job-1");
        proxy.Dispose();
        var act = () => proxy.Dispose();
        act.Should().NotThrow("double-dispose must be safe");
    }

    // ── Caching ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequestTokenRefreshAsync_CacheHit_DoesNotInvokeDelegate()
    {
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                callCount++;
                return Task.FromResult(new TokenRefreshResponse { Token = "tok", ExpiresAt = FutureExpiry });
            });

        // First call — cache miss
        var t1 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        // Second call — should hit cache
        var t2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        callCount.Should().Be(1, "second call should use cached token");
        t1.Should().Be("tok");
        t2.Should().Be("tok");
    }

    // ── Single-flight ──────────────────────────────────────────────────────

    [Fact]
    public async Task RequestTokenRefreshAsync_ConcurrentSameKind_InvokesDelegateOnce()
    {
        var callCount = 0;
        var gate = new TaskCompletionSource<TokenRefreshResponse>(TaskCreationOptions.RunContinuationsAsynchronously);

        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, ct) =>
            {
                Interlocked.Increment(ref callCount);
                return gate.Task;
            });

        // Launch 5 concurrent callers before the gate completes
        var tasks = Enumerable.Range(0, 5)
            .Select(_ => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None))
            .ToList();

        // TODO [WARNING]: `await Task.Delay(50)` is a timing-sensitive synchronisation mechanism.
        // On a loaded CI runner the delegate may complete before all 5 callers acquire the lock
        // and observe the in-flight entry, causing the single-flight guarantee not to be exercised.
        // Replace with a ManualResetEventSlim or a semaphore that counts all callers before releasing
        // the gate, to make this test deterministic. (Test Quality Review)
        // Give them all a moment to land
        await Task.Delay(50);

        // Release the gate
        gate.SetResult(new TokenRefreshResponse { Token = "single", ExpiresAt = FutureExpiry });

        var results = await Task.WhenAll(tasks);

        callCount.Should().Be(1, "all concurrent callers should share one in-flight request");
        results.Should().AllSatisfy(t => t.Should().Be("single"));
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_DifferentKinds_EachInvokesDelegate()
    {
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (kind, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromResult(new TokenRefreshResponse { Token = kind.ToString(), ExpiresAt = FutureExpiry });
            });

        var t1 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var t2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Brain, CancellationToken.None);

        callCount.Should().Be(2, "different kinds are independent — each issues its own request");
        t1.Should().Be(ProviderKind.Repository.ToString());
        t2.Should().Be(ProviderKind.Brain.ToString());
    }

    // ── Fault handling ─────────────────────────────────────────────────────

    [Fact]
    public async Task RequestTokenRefreshAsync_FaultedDelegate_NotCached_RethrownAndNextCallRetries()
    {
        // With the transient retry, a single non-HubException failure is retried within the
        // same call. To test that a faulted in-flight task is removed from the cache (so the
        // next separate RequestTokenRefreshAsync call re-issues the request), we use a
        // HubException (which is NOT retried) so the first call definitively fails.
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                if (callCount == 1)
                    return Task.FromException<TokenRefreshResponse>(
                        new Microsoft.AspNetCore.SignalR.HubException("permanent error"));
                return Task.FromResult(new TokenRefreshResponse { Token = "ok", ExpiresAt = FutureExpiry });
            });

        // First call faults with a HubException (permanent — not retried)
        var act = async () => await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>(
            "HubException (permanent error) must be rethrown immediately");

        // Second call should succeed — the faulted in-flight task was removed from the cache
        var token = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        callCount.Should().Be(2, "faulted task removed from in-flight — next call issues a new request");
        token.Should().Be("ok");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_Concurrent_FaultedDelegate_AllCallersFault()
    {
        var gate = new TaskCompletionSource<TokenRefreshResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) => gate.Task);

        var tasks = Enumerable.Range(0, 3)
            .Select(_ => proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None))
            .ToList();

        await Task.Delay(20);

        // Fault the shared in-flight task with a HubException (permanent — not retried)
        // so the test completes promptly without triggering the transient retry delays.
        gate.SetException(new Microsoft.AspNetCore.SignalR.HubException("server error"));

        foreach (var t in tasks)
        {
            var task = t;
            var act = async () => await task;
            await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>();
        }
    }

    // ── Near-expiry token triggers refresh ────────────────────────────────

    [Fact]
    public async Task RequestTokenRefreshAsync_NearExpiry_TriggersRefresh()
    {
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                // First call returns near-expiry token; second returns a fresh one
                var expiry = callCount == 1 ? NearExpiry : FutureExpiry;
                return Task.FromResult(new TokenRefreshResponse { Token = $"tok-{callCount}", ExpiresAt = expiry });
            });

        var first = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        var second = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        callCount.Should().Be(2, "near-expiry cached token should trigger a refresh");
        first.Should().Be("tok-1");
        second.Should().Be("tok-2");
    }

    // ── Transient retry (non-HubException) ────────────────────────────────

    [Fact]
    public async Task RequestTokenRefreshAsync_TransientNonHubException_IsRetried()
    {
        // A transient failure (e.g. connection blip) that is NOT a HubException should be
        // retried by FetchTokenFromHubAsync up to TokenVendMaxRetries times.
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                if (callCount < 3)
                    return Task.FromException<TokenRefreshResponse>(new InvalidOperationException("transient blip"));
                return Task.FromResult(new TokenRefreshResponse { Token = "recovered", ExpiresAt = FutureExpiry });
            });

        // Should succeed after retries (TokenVendMaxRetries = 2, so 3 total attempts)
        var token = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);

        callCount.Should().Be(3, "two transient failures should be retried, third attempt succeeds");
        token.Should().Be("recovered");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_HubException_IsNotRetried()
    {
        // HubException signals a permanent server-side error (bad config, no auth method).
        // It must NOT be retried — it should propagate immediately.
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                return Task.FromException<TokenRefreshResponse>(
                    new Microsoft.AspNetCore.SignalR.HubException("Provider config not found"));
            });

        var act = async () => await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<Microsoft.AspNetCore.SignalR.HubException>(
            "HubException (permanent server error) must propagate immediately without retry");

        callCount.Should().Be(1, "HubException should not trigger any retry attempts");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_TransientExhaustsRetries_Rethrows()
    {
        // When all retry attempts are exhausted the last transient exception should be rethrown.
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (_, _, _) =>
            {
                Interlocked.Increment(ref callCount);
                // Always fail with a transient error — exceeds TokenVendMaxRetries
                return Task.FromException<TokenRefreshResponse>(new InvalidOperationException($"blip {callCount}"));
            });

        var act = async () => await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "after exhausting retries the last transient exception should be rethrown");

        // 1 initial + 2 retries = 3 total attempts (TokenVendMaxRetries = 2)
        callCount.Should().Be(3, "should attempt initial call plus TokenVendMaxRetries retries");
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 1000)]
    public void ResolveTokenVendRetryDelay_WithoutOverride_ShouldBackOffExponentially(int attempt, int expectedMilliseconds)
    {
        OrchestratorProxy.ResolveTokenVendRetryDelay(attempt, testOverride: null)
            .Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ResolveTokenVendRetryDelay_WithOverride_ShouldUseOverride(int attempt)
    {
        var testOverride = TimeSpan.FromMilliseconds(1);

        OrchestratorProxy.ResolveTokenVendRetryDelay(attempt, testOverride).Should().Be(testOverride);
    }

    // ── Waiting out a hub reconnect ───────────────────────────────────────

    private static readonly TimeSpan ReconnectPollInterval = TimeSpan.FromMilliseconds(1);

    [Fact]
    public async Task WaitWhileHubReconnecting_ReturnsOnceConnected()
    {
        var states = new Queue<HubConnectionState>(
            [HubConnectionState.Reconnecting, HubConnectionState.Reconnecting, HubConnectionState.Connected]);
        var reads = 0;

        await OrchestratorProxy.WaitWhileHubReconnectingAsync(
            () =>
            {
                reads++;
                return states.Dequeue();
            },
            ReconnectPollInterval, "job", CancellationToken.None);

        reads.Should().Be(3, "the state is read once per poll: two Reconnecting reads, then Connected");
    }

    [Fact]
    public async Task WaitWhileHubReconnecting_Connecting_WaitsUntilConnected()
    {
        var states = new Queue<HubConnectionState>([HubConnectionState.Connecting, HubConnectionState.Connected]);
        var reads = 0;

        await OrchestratorProxy.WaitWhileHubReconnectingAsync(
            () =>
            {
                reads++;
                return states.Dequeue();
            },
            ReconnectPollInterval, "job", CancellationToken.None);

        reads.Should().Be(2, "a Connecting hub is waited on until it reports Connected");
    }

    [Fact]
    public async Task WaitWhileHubReconnecting_Connected_ReturnsImmediately()
    {
        var reads = 0;

        await OrchestratorProxy.WaitWhileHubReconnectingAsync(
            () =>
            {
                reads++;
                return HubConnectionState.Connected;
            },
            ReconnectPollInterval, "job", CancellationToken.None);

        reads.Should().Be(1, "a connected hub needs no wait");
    }

    [Fact]
    public async Task WaitWhileHubReconnecting_Disconnected_ReturnsImmediately()
    {
        // A Disconnected connection is closed and never comes back, so waiting on it would hang the run.
        var reads = 0;

        await OrchestratorProxy.WaitWhileHubReconnectingAsync(
            () =>
            {
                reads++;
                return HubConnectionState.Disconnected;
            },
            ReconnectPollInterval, "job", CancellationToken.None);

        reads.Should().Be(1, "a disconnected hub must not be waited on");
    }

    [Fact]
    public async Task WaitWhileHubReconnecting_CancelledWhileWaiting_Throws()
    {
        using var cts = new CancellationTokenSource();
        var reads = 0;

        var act = async () => await OrchestratorProxy.WaitWhileHubReconnectingAsync(
            () =>
            {
                // Cancel on the second read, so the token is cancelled while the helper is already waiting.
                if (++reads == 2)
                    cts.Cancel();
                return HubConnectionState.Reconnecting;
            },
            ReconnectPollInterval, "job", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            "cancelling the token (SIGTERM, operator cancel) must end the wait");
    }

    [Fact]
    public async Task RequestTokenRefreshAsync_NoDelegate_DisconnectedHub_FailsInsteadOfWaiting()
    {
        // An unstarted connection reads Disconnected. It never comes back, so the token request must
        // fail through the real call path as it did before the reconnect wait was added, not hang.
        var proxy = new OrchestratorProxy(BuildDummyConnection(), "job");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var act = async () => await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, cts.Token);

        await act.Should().ThrowAsync<InvalidOperationException>();
        cts.IsCancellationRequested.Should().BeFalse("a disconnected hub must fail fast, not wait for cancellation");
    }

    // ── Cache key isolation: same kind, different includeIssuePermission ──

    [Fact]
    public async Task RequestTokenRefreshAsync_SameKind_DifferentIncludeIssuePermission_CachedIndependently()
    {
        // Verifies that (Repository, false) and (Repository, true) are cached under separate
        // keys. A false-scoped token must never be served for a true request and vice versa.
        var callCount = 0;
        var proxy = new OrchestratorProxy(
            BuildDummyConnection(), "job",
            (kind, includeIssue, _) =>
            {
                Interlocked.Increment(ref callCount);
                var token = includeIssue ? "token-with-issues" : "token-without-issues";
                return Task.FromResult(new TokenRefreshResponse { Token = token, ExpiresAt = FutureExpiry });
            });

        // First pair: populate both cache slots
        var tokenA = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None, includeIssuePermission: false);
        var tokenB = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None, includeIssuePermission: true);

        callCount.Should().Be(2, "false and true requests must each issue their own hub call");
        tokenA.Should().Be("token-without-issues");
        tokenB.Should().Be("token-with-issues");

        // Second pair: both must hit cache (delegate not called again)
        var tokenA2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None, includeIssuePermission: false);
        var tokenB2 = await proxy.RequestTokenRefreshAsync(ProviderKind.Repository, CancellationToken.None, includeIssuePermission: true);

        callCount.Should().Be(2, "second calls for both variants must use cache");
        tokenA2.Should().Be("token-without-issues", "false path must return its own cached token");
        tokenB2.Should().Be("token-with-issues", "true path must return its own cached token");
    }
}
