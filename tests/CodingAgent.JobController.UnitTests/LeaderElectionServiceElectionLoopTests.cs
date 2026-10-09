using AwesomeAssertions;
using Microsoft.Extensions.Options;

namespace CodingAgent.JobController.UnitTests;

/// <summary>
/// Drives the Kubernetes branch of <see cref="LeaderElectionService"/> (RunElectionLoopAsync,
/// HandleStartedLeading, StopAsync) through the internal lock-factory constructor and an in-memory
/// <see cref="FakeLeaseLock"/>, so no API server is needed. The k8s LeaderElector uses real time,
/// so the options are short and every wait has a 10 s ceiling.
/// </summary>
public sealed class LeaderElectionServiceElectionLoopTests
{
    private const string PodIdentity = "pod-a";
    private const string OtherPodIdentity = "pod-b";
    private static readonly TimeSpan WaitCeiling = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task LeaderToken_KubernetesModeBeforeFirstAcquisition_IsCancelled()
    {
        var fakeLock = new FakeLeaseLock(PodIdentity);
        fakeLock.StealLease(OtherPodIdentity);
        using var sut = CreateSut(fakeLock);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            sut.IsLeader.Should().BeFalse("pod-b holds the lease");
            // TODO: [WARNING] On weakly-ordered architectures, reading LeaderToken here without a memory barrier
            // could observe a stale value if a future implementation assigned _leaderCts in StartAsync (regression
            // of the exact bug being fixed). Adding Thread.MemoryBarrier() or Volatile.Read before this access
            // would make the test reliably catch mutation (c) from the requirements. Safe in practice on x86/x64
            // and because the fixed implementation does not touch _leaderCts in StartAsync.
            sut.LeaderToken.IsCancellationRequested.Should().BeTrue(
                "LeaderToken must be cancelled while this instance is not the leader");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ElectionLoop_FreeLease_AcquiresLeadershipAndIssuesLiveToken()
    {
        var fakeLock = new FakeLeaseLock(PodIdentity);
        string? lockNamespace = null;
        string? lockIdentity = null;
        using var sut = CreateSut(fakeLock, (ns, identity) => (lockNamespace, lockIdentity) = (ns, identity));
        using var started = new SemaphoreSlim(0);
        var startedCount = 0;
        bool? isLeaderWhenStartedFired = null;
        bool? tokenCancelledWhenStartedFired = null;
        sut.OnStartedLeading += () =>
        {
            Interlocked.Increment(ref startedCount);
            isLeaderWhenStartedFired = sut.IsLeader;
            tokenCancelledWhenStartedFired = sut.LeaderToken.IsCancellationRequested;
            started.Release();
        };

        await sut.StartAsync(CancellationToken.None);
        try
        {
            (await started.WaitAsync(WaitCeiling)).Should().BeTrue("the free lease must be acquired");
            sut.IsLeader.Should().BeTrue();
            sut.LeaderToken.IsCancellationRequested.Should().BeFalse("the term token is live while leading");
            isLeaderWhenStartedFired.Should().BeTrue("IsLeader is set before OnStartedLeading fires");
            tokenCancelledWhenStartedFired.Should().BeFalse("the term token exists before OnStartedLeading fires");
            startedCount.Should().Be(1);
            fakeLock.CurrentHolder.Should().Be(PodIdentity);
            // TODO: [WARNING] lockNamespace and lockIdentity are plain string? locals written on the background
            // election-loop thread (inside the lock-factory callback) and read here on the test thread. The
            // SemaphoreSlim WaitAsync provides a happens-before edge in practice, but lockNamespace/lockIdentity
            // are not volatile. On weakly-ordered architectures the JIT could in theory hoist these reads above
            // the semaphore, causing spurious null assertions. Fix: capture the values inside the OnStartedLeading
            // handler (after the semaphore release establishes the barrier) or use Volatile.Read at the read site.
            lockNamespace.Should().Be("test-ns");
            lockIdentity.Should().Be(PodIdentity);
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ElectionLoop_LeaseTakenByAnotherPod_EndsTermExactlyOnce()
    {
        var fakeLock = new FakeLeaseLock(PodIdentity);
        using var sut = CreateSut(fakeLock);
        using var started = new SemaphoreSlim(0);
        using var stopped = new SemaphoreSlim(0);
        var stoppedCount = 0;
        var termToken = CancellationToken.None;
        bool? tokenCancelledWhenStoppedFired = null;
        bool? isLeaderWhenStoppedFired = null;
        sut.OnStartedLeading += () => started.Release();
        sut.OnStoppedLeading += () =>
        {
            Interlocked.Increment(ref stoppedCount);
            tokenCancelledWhenStoppedFired = termToken.IsCancellationRequested;
            isLeaderWhenStoppedFired = sut.IsLeader;
            stopped.Release();
        };

        await sut.StartAsync(CancellationToken.None);
        try
        {
            (await started.WaitAsync(WaitCeiling)).Should().BeTrue("the free lease must be acquired");
            termToken = sut.LeaderToken;

            fakeLock.StealLease(OtherPodIdentity);

            (await stopped.WaitAsync(WaitCeiling)).Should().BeTrue("renewals fail once pod-b holds the lease");
            sut.IsLeader.Should().BeFalse();
            termToken.IsCancellationRequested.Should().BeTrue("the token handed out during the term must be cancelled on loss");
            sut.LeaderToken.IsCancellationRequested.Should().BeTrue("a LeaderToken read after the loss must already be cancelled");
            tokenCancelledWhenStoppedFired.Should().BeTrue("the term token is cancelled before OnStoppedLeading fires");
            isLeaderWhenStoppedFired.Should().BeFalse("IsLeader is false before OnStoppedLeading fires");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        stoppedCount.Should().Be(1, "one lost term raises OnStoppedLeading once, and StopAsync must not raise it again");
    }

    [Fact]
    public async Task ElectionLoop_LeaseFreedAfterLoss_StartsNewTermWithFreshToken()
    {
        var fakeLock = new FakeLeaseLock(PodIdentity);
        using var sut = CreateSut(fakeLock);
        using var started = new SemaphoreSlim(0);
        using var stopped = new SemaphoreSlim(0);
        var startedCount = 0;
        sut.OnStartedLeading += () =>
        {
            Interlocked.Increment(ref startedCount);
            started.Release();
        };
        sut.OnStoppedLeading += () => stopped.Release();

        await sut.StartAsync(CancellationToken.None);
        try
        {
            (await started.WaitAsync(WaitCeiling)).Should().BeTrue("the free lease must be acquired");
            var firstTermToken = sut.LeaderToken;

            fakeLock.StealLease(OtherPodIdentity);
            (await stopped.WaitAsync(WaitCeiling)).Should().BeTrue("renewals fail once pod-b holds the lease");

            fakeLock.ReleaseLease();
            (await started.WaitAsync(WaitCeiling)).Should().BeTrue("the freed lease must be acquired again");

            var secondTermToken = sut.LeaderToken;
            sut.IsLeader.Should().BeTrue();
            startedCount.Should().Be(2);
            secondTermToken.IsCancellationRequested.Should().BeFalse("the new term gets a live token");
            firstTermToken.IsCancellationRequested.Should().BeTrue("the first term's token stays cancelled");
            secondTermToken.Should().NotBe(firstTermToken, "each term gets its own token");
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_WhileLeading_CancelsTokenAndRaisesOnStoppedLeadingOnce()
    {
        var fakeLock = new FakeLeaseLock(PodIdentity);
        using var sut = CreateSut(fakeLock);
        using var started = new SemaphoreSlim(0);
        var stoppedCount = 0;
        sut.OnStartedLeading += () => started.Release();
        sut.OnStoppedLeading += () => Interlocked.Increment(ref stoppedCount);

        await sut.StartAsync(CancellationToken.None);
        (await started.WaitAsync(WaitCeiling)).Should().BeTrue("the free lease must be acquired");
        var termToken = sut.LeaderToken;

        await sut.StopAsync(CancellationToken.None);

        sut.IsLeader.Should().BeFalse();
        termToken.IsCancellationRequested.Should().BeTrue("StopAsync must cancel the term token");
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue("LeaderToken stays cancelled after StopAsync");
        stoppedCount.Should().Be(1, "StopAsync and the election loop must not both raise OnStoppedLeading");
    }

    private static LeaderElectionService CreateSut(
        FakeLeaseLock fakeLock, Action<string, string>? onLockCreated = null)
    {
        var options = Options.Create(new LeaderElectionOptions
        {
            LeaseName = "test-lease",
            Namespace = "test-ns",
            Identity = PodIdentity,
            LeaseDuration = TimeSpan.FromSeconds(5),
            RenewDeadline = TimeSpan.FromSeconds(1),
            RetryPeriod = TimeSpan.FromMilliseconds(50),
        });

        return new LeaderElectionService(options, lockFactory: (ns, identity) =>
        {
            onLockCreated?.Invoke(ns, identity);
            return fakeLock;
        });
    }
}
