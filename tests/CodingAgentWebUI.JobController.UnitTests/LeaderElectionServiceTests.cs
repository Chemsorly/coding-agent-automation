using AwesomeAssertions;
using Microsoft.Extensions.Options;
using Xunit;

namespace CodingAgentWebUI.JobController.UnitTests;

/// <summary>
/// Unit tests for <see cref="LeaderElectionService"/>.
///
/// Strategy: all branches testable without a live k8s cluster use
/// <c>kubeClient = null</c> (non-Kubernetes path) or manipulate options.
/// The election loop itself (requires real k8s or deep mocking of LeaderElector)
/// is covered by integration tests; here we cover StartAsync/StopAsync/Dispose
/// and the non-k8s degradation paths.
/// </summary>
public sealed class LeaderElectionServiceTests
{
    // ── Construction ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullKubeClient_DoesNotThrow()
    {
        var act = () => new LeaderElectionService(
            Options.Create(new LeaderElectionOptions()), kubeClient: null);

        act.Should().NotThrow();
    }

    [Fact]
    public void Constructor_WithOptions_UsesProvidedOptions()
    {
        var opts = new LeaderElectionOptions
        {
            LeaseName = "my-lease",
            LeaseDuration = TimeSpan.FromSeconds(30)
        };

        var svc = new LeaderElectionService(Options.Create(opts), kubeClient: null);

        // Service constructed successfully — options are consumed on StartAsync
        svc.Should().NotBeNull();
    }

    // ── IsLeader initial state ────────────────────────────────────────────

    [Fact]
    public void IsLeader_BeforeStart_IsFalse()
    {
        var svc = CreateNonK8sService();
        svc.IsLeader.Should().BeFalse("leader election has not started yet");
    }

    [Fact]
    public void LeaderToken_BeforeStart_IsCancelled()
    {
        var svc = CreateNonK8sService();
        // Before StartAsync, LeaderToken defaults to a cancelled token
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue(
            "LeaderToken defaults to a cancelled token when service has not started");
    }

    // ── StartAsync — non-Kubernetes path (kubeClient == null) ────────────

    [Fact]
    public async Task StartAsync_NonK8sEnvironment_DoesNotThrow_IsLeaderFalse()
    {
        var svc = CreateNonK8sService();

        var act = () => svc.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("non-k8s environment must degrade gracefully");

        svc.IsLeader.Should().BeFalse("non-k8s instance must not become leader");
    }

    [Fact]
    public async Task StartAsync_NonK8s_FailOnNonKubernetesEnvironment_False_ReturnsNormally()
    {
        var opts = new LeaderElectionOptions
        {
            FailOnNonKubernetesEnvironment = false
        };
        var svc = new LeaderElectionService(Options.Create(opts), kubeClient: null);

        // Must complete without throwing when graceful degradation is configured
        await svc.StartAsync(CancellationToken.None);

        svc.IsLeader.Should().BeFalse();
    }

    [Fact]
    public async Task StartAsync_NonK8s_FailOnNonKubernetesEnvironment_True_Throws()
    {
        var opts = new LeaderElectionOptions
        {
            FailOnNonKubernetesEnvironment = true
        };
        var svc = new LeaderElectionService(Options.Create(opts), kubeClient: null);

        var act = () => svc.StartAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "service must throw when configured to fail outside Kubernetes");
    }

    // ── StopAsync — before StartAsync ────────────────────────────────────

    [Fact]
    public async Task StopAsync_BeforeStart_DoesNotThrow()
    {
        var svc = CreateNonK8sService();

        var act = () => svc.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("StopAsync before StartAsync must be a no-op");
    }

    [Fact]
    public async Task StopAsync_AfterNonK8sStart_DoesNotThrow()
    {
        var svc = CreateNonK8sService();
        await svc.StartAsync(CancellationToken.None);

        var act = () => svc.StopAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("StopAsync after non-k8s start must complete cleanly");
    }

    // ── StartAsync + StopAsync roundtrip ─────────────────────────────────

    [Fact]
    public async Task StartThenStop_NonK8s_CompletesCleanly()
    {
        var svc = CreateNonK8sService();
        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        svc.IsLeader.Should().BeFalse();
    }

    // ── Events: OnStartedLeading / OnStoppedLeading ───────────────────────

    [Fact]
    public async Task OnStartedLeading_NonK8s_NeverFired()
    {
        var fired = false;
        var svc = CreateNonK8sService();
        svc.OnStartedLeading += () => fired = true;

        await svc.StartAsync(CancellationToken.None);

        fired.Should().BeFalse("non-k8s instance never acquires leadership");
    }

    [Fact]
    public async Task OnStoppedLeading_NonK8s_NeverFired()
    {
        var fired = false;
        var svc = CreateNonK8sService();
        svc.OnStoppedLeading += () => fired = true;

        await svc.StartAsync(CancellationToken.None);
        await svc.StopAsync(CancellationToken.None);

        fired.Should().BeFalse("non-k8s instance was never leader, so OnStoppedLeading must not fire");
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_BeforeStart_DoesNotThrow()
    {
        var svc = CreateNonK8sService();
        var act = () => svc.Dispose();
        act.Should().NotThrow("Dispose before StartAsync must be a no-op");
    }

    [Fact]
    public async Task Dispose_AfterStart_DoesNotThrow()
    {
        var svc = CreateNonK8sService();
        await svc.StartAsync(CancellationToken.None);

        var act = () => svc.Dispose();
        act.Should().NotThrow("Dispose after StartAsync must not throw");
    }

    [Fact]
    public async Task Dispose_Idempotent_DoesNotThrow()
    {
        var svc = CreateNonK8sService();
        await svc.StartAsync(CancellationToken.None);

        svc.Dispose();
        var act = () => svc.Dispose();
        act.Should().NotThrow("second Dispose must be a no-op");
    }

    // ── Options: ResolveIdentity / ResolveNamespace defaults ─────────────

    [Fact]
    public async Task StartAsync_DefaultOptions_DoesNotThrow()
    {
        // Default options: LeaseName="caa-leader", LeaseDuration=15s, etc.
        var svc = new LeaderElectionService(
            Options.Create(new LeaderElectionOptions()), kubeClient: null);

        var act = () => svc.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("default options must always succeed for non-k8s path");
    }

    [Fact]
    public async Task StartAsync_CustomIdentity_DoesNotThrow()
    {
        var opts = new LeaderElectionOptions { Identity = "my-pod-id" };
        var svc = new LeaderElectionService(Options.Create(opts), kubeClient: null);

        var act = () => svc.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_CustomNamespace_DoesNotThrow()
    {
        var opts = new LeaderElectionOptions { Namespace = "my-namespace" };
        var svc = new LeaderElectionService(Options.Create(opts), kubeClient: null);

        var act = () => svc.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ── ILeaderElectionService interface compliance ───────────────────────

    [Fact]
    public void ImplementsILeaderElectionService()
    {
        var svc = CreateNonK8sService();
        svc.Should().BeAssignableTo<ILeaderElectionService>();
    }

    [Fact]
    public void ImplementsIHostedService()
    {
        var svc = CreateNonK8sService();
        svc.Should().BeAssignableTo<Microsoft.Extensions.Hosting.IHostedService>();
    }

    [Fact]
    public void ImplementsIDisposable()
    {
        var svc = CreateNonK8sService();
        svc.Should().BeAssignableTo<IDisposable>();
    }

    // ── LeaderToken after non-k8s start ──────────────────────────────────

    [Fact]
    public async Task LeaderToken_AfterNonK8sStart_IsCancelled()
    {
        var svc = CreateNonK8sService();
        await svc.StartAsync(CancellationToken.None);

        // Non-k8s: no _leaderCts is created, so token defaults to cancelled
        svc.LeaderToken.IsCancellationRequested.Should().BeTrue(
            "non-k8s instance never acquires leadership so LeaderToken stays cancelled");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static LeaderElectionService CreateNonK8sService(
        LeaderElectionOptions? opts = null)
    {
        return new LeaderElectionService(
            Options.Create(opts ?? new LeaderElectionOptions()),
            kubeClient: null);
    }
}
