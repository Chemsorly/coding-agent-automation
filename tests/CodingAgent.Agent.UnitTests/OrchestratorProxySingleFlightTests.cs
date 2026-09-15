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
            (_, _) =>
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
            (_, ct) =>
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
            (kind, _) =>
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
            (_, _) =>
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
            (_, _) => gate.Task);

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
            (_, _) =>
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
            (_, _) =>
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
            (_, _) =>
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
            (_, _) =>
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
}
