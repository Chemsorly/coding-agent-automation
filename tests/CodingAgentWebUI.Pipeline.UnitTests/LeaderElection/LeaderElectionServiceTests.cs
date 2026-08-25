using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.LeaderElection;
using Microsoft.Extensions.Options;

namespace CodingAgentWebUI.Pipeline.UnitTests.LeaderElection;

/// <summary>
/// Tests for <see cref="LeaderElectionService"/> covering the non-Kubernetes paths
/// that don't require a live cluster.
/// </summary>
public sealed class LeaderElectionServiceTests : IDisposable
{
    private static IOptions<LeaderElectionOptions> DefaultOptions(Action<LeaderElectionOptions>? configure = null)
    {
        var opts = new LeaderElectionOptions();
        configure?.Invoke(opts);
        return Options.Create(opts);
    }

    // ── Constructor / initial state ───────────────────────────────────────────

    [Fact]
    public void IsLeader_InitiallyFalse()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public void LeaderToken_InitiallyReturnsCancelledToken()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        // Before StartAsync, _leaderCts is null → property returns new CancellationToken(canceled: true)
        sut.LeaderToken.IsCancellationRequested.Should().BeTrue();
    }

    // ── Non-Kubernetes path (kubeClient = null) ───────────────────────────────

    [Fact]
    public async Task StartAsync_NonKubernetes_GracefulDegradation_CompletesImmediately()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        // No kubeClient → graceful degradation (FailOnNonKubernetesEnvironment = false by default)
        await sut.StartAsync(CancellationToken.None);
        // Should complete synchronously and not throw
        sut.IsLeader.Should().BeFalse("non-K8s path never becomes leader");
    }

    [Fact]
    public async Task StartAsync_NonKubernetes_FailOnNonK8s_ThrowsInvalidOperation()
    {
        using var sut = new LeaderElectionService(DefaultOptions(o =>
            o.FailOnNonKubernetesEnvironment = true));

        var act = () => sut.StartAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Kubernetes*");
    }

    [Fact]
    public async Task StartAsync_NonKubernetes_RemainsNonLeaderAfterStart()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        await sut.StartAsync(CancellationToken.None);
        sut.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task StopAsync_WhenNotStarted_DoesNotThrow()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        var ex = await Record.ExceptionAsync(() => sut.StopAsync(CancellationToken.None));
        ex.Should().BeNull("StopAsync before StartAsync must be a safe no-op");
    }

    [Fact]
    public async Task StopAsync_AfterGracefulStart_DoesNotThrow()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        await sut.StartAsync(CancellationToken.None);
        var ex = await Record.ExceptionAsync(() => sut.StopAsync(CancellationToken.None));
        ex.Should().BeNull();
    }

    // ── Cancellation via CancellationToken ────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithPreCancelledToken_NonKubernetes_CompletesImmediately()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var ex = await Record.ExceptionAsync(() => sut.StartAsync(cts.Token));
        ex.Should().BeNull("non-K8s path must complete without hanging on a cancelled token");
    }

    // ── Events ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnStartedLeading_NotFired_InNonKubernetesMode()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        var fired = false;
        sut.OnStartedLeading += () => fired = true;

        await sut.StartAsync(CancellationToken.None);

        fired.Should().BeFalse("non-K8s path never fires OnStartedLeading");
    }

    [Fact]
    public async Task OnStoppedLeading_NotFired_WhenNeverLeader()
    {
        using var sut = new LeaderElectionService(DefaultOptions());
        var fired = false;
        sut.OnStoppedLeading += () => fired = true;

        await sut.StartAsync(CancellationToken.None);
        await sut.StopAsync(CancellationToken.None);

        fired.Should().BeFalse("OnStoppedLeading is only fired when transitioning from leader to non-leader");
    }

    // ── Dispose ───────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_CalledMultipleTimes_DoesNotThrow()
    {
        var sut = new LeaderElectionService(DefaultOptions());
        sut.Dispose();
        var ex = Record.Exception(() => sut.Dispose());
        ex.Should().BeNull("second Dispose must be a safe no-op");
    }

    [Fact]
    public async Task Dispose_AfterStart_DoesNotThrow()
    {
        var sut = new LeaderElectionService(DefaultOptions());
        await sut.StartAsync(CancellationToken.None);
        var ex = Record.Exception(() => sut.Dispose());
        ex.Should().BeNull();
    }

    // ── Options / identity resolution ─────────────────────────────────────────

    [Fact]
    public async Task StartAsync_WithCustomLeaseName_DoesNotThrow()
    {
        using var sut = new LeaderElectionService(DefaultOptions(o =>
        {
            o.LeaseName = "custom-lease";
            o.Namespace = "custom-ns";
            o.Identity = "my-pod";
        }));
        var ex = await Record.ExceptionAsync(() => sut.StartAsync(CancellationToken.None));
        ex.Should().BeNull();
    }

    [Fact]
    public void Options_DefaultValues_AreCorrect()
    {
        // Migrated from Services/LeaderElectionServiceTests.cs — that file was a
        // near-duplicate; this test had no counterpart in LeaderElection/.
        var opts = new LeaderElectionOptions();

        opts.LeaseName.Should().Be("caa-leader");
        opts.Namespace.Should().BeNull();
        opts.LeaseDuration.Should().Be(TimeSpan.FromSeconds(15));
        opts.RenewDeadline.Should().Be(TimeSpan.FromSeconds(10));
        opts.RetryPeriod.Should().Be(TimeSpan.FromSeconds(2));
        opts.Identity.Should().BeNull();
        opts.FailOnNonKubernetesEnvironment.Should().BeFalse();
    }

    public void Dispose()
    {
        // Intentionally empty — each test creates its own sut via using/await using
    }
}
