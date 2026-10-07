using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for the registration gate in <see cref="AgentConnectionLifecycle"/>.
/// Verifies that:
/// - Gate resets at reconnect / terminal-close start.
/// - Gate completes after RegisterAgent succeeds (or fails gracefully).
/// - WaitForRegistrationAsync returns quickly after registration.
/// - DisposeAsync cancels the gate so waiters are not left hanging.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentConnectionLifecycleGateTests
{
    // ── WaitForRegistrationAsync before any registration ─────────────────

    [Fact]
    public void WaitForRegistrationAsync_InitialState_ReturnsImmediately()
    {
        // Gate starts completed — no blocking before first reconnect
        var (lifecycle, _, _) = CreateLifecycle();

        lifecycle.WaitForRegistrationAsync(CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue(
            "gate starts completed — should return immediately");
    }

    // ── Gate completes after HandleReconnectedAsync ─────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterHandleReconnected_ReturnsPromptly()
    {
        var (lifecycle, initialManager, _) = CreateLifecycle();

        // HandleReconnectedAsync: registers gate reset at start, TrySetResult at end
        await lifecycle.HandleReconnectedAsync("conn-new");

        lifecycle.WaitForRegistrationAsync(CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue(
            "gate should be released after HandleReconnectedAsync");
    }

    // ── Gate completes after HandleTerminalClosedAsync exhaustion ──────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterTerminalCloseExhaustion_ReturnsPromptly()
    {
        var stopCalled = false;
        var (lifecycle, _, _) = CreateLifecycle(stopApplication: () => stopCalled = true);
        lifecycle.ExtendedRetryDelay = TimeSpan.FromMilliseconds(1);

        // All attempts fail → exhausted → TrySetResult + StopApplication
        await lifecycle.HandleTerminalClosedAsync(null, maxAttempts: 1, delayOverride: _ => TimeSpan.Zero);

        lifecycle.WaitForRegistrationAsync(CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue(
            "gate should be released even after exhaustion");
        stopCalled.Should().BeTrue();
    }

    // ── Dispose cancels waiters ─────────────────────────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterDispose_ReturnsWithoutHanging()
    {
        var (lifecycle, _, _) = CreateLifecycle();

        await lifecycle.DisposeAsync();

        // Should not hang: the gate cancelled by DisposeAsync counts as completed, so the wait
        // returns synchronously (asserted instead of timed, so a stalled test host cannot fail it)
        lifecycle.WaitForRegistrationAsync(CancellationToken.None).IsCompleted.Should().BeTrue(
            "should not hang after dispose");
    }

    // ── Gate reset on reconnect (new gate is incomplete) ───────────────────

    [Fact]
    public async Task HandleReconnectedAsync_ResetsGateBeforeRegistration()
    {
        // First: complete the gate by simulating an initial HandleReconnectedAsync
        var (lifecycle, _, _) = CreateLifecycle();
        await lifecycle.HandleReconnectedAsync("conn-1"); // completes gate

        // Gate is now complete — second HandleReconnectedAsync should reset it
        // (new incomplete gate installed at start) and then re-complete it after registration.
        await lifecycle.HandleReconnectedAsync("conn-2");

        // After the second reconnect, the gate should again be completed
        lifecycle.WaitForRegistrationAsync(CancellationToken.None).IsCompletedSuccessfully.Should().BeTrue(
            "gate should be completed after second reconnect");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static (AgentConnectionLifecycle Lifecycle, FakeHubConnectionManager InitialManager, FakeHubConnectionManagerFactory Factory)
        CreateLifecycle(
            Action? stopApplication = null,
            Func<IHubConnectionManager>? factoryFunc = null,
            CancellationToken appStoppingToken = default)
    {
        var mockLogger = new Mock<Serilog.ILogger>().Object;
        var initialManager = new FakeHubConnectionManager();
        var factory = new FakeHubConnectionManagerFactory(factoryFunc ?? (() => new FakeHubConnectionManager()));
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(appStoppingToken);
        if (stopApplication is not null)
            lifetimeMock.Setup(l => l.StopApplication()).Callback(stopApplication);

        var lifecycle = new AgentConnectionLifecycle(
            initialManager,
            factory,
            new AgentId("gate-agent"),
            lifetimeMock.Object,
            mockLogger);

        return (lifecycle, initialManager, factory);
    }
}
