using AwesomeAssertions;
using CodingAgent.Infrastructure.Resilience;
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
    public async Task WaitForRegistrationAsync_InitialState_ReturnsImmediately()
    {
        // Gate starts completed — no blocking before first reconnect
        var (lifecycle, _, _) = CreateLifecycle();

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(100,
            "gate starts completed — should return immediately");
    }

    // ── Gate completes after HandleReconnectedAsync ─────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterHandleReconnected_ReturnsPromptly()
    {
        var (lifecycle, initialManager, _) = CreateLifecycle();

        // HandleReconnectedAsync: registers gate reset at start, TrySetResult at end
        await lifecycle.HandleReconnectedAsync("conn-new");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "gate should be released even after exhaustion");
        stopCalled.Should().BeTrue();
    }

    // ── Dispose cancels waiters ─────────────────────────────────────────────

    [Fact]
    public async Task WaitForRegistrationAsync_AfterDispose_ReturnsWithoutHanging()
    {
        var (lifecycle, _, _) = CreateLifecycle();

        await lifecycle.DisposeAsync();

        // Should not hang — either returns immediately or throws OperationCanceledException
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.WaitForRegistrationAsync(CancellationToken.None)
            .ContinueWith(_ => { }); // swallow cancellation
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(1000,
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
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await lifecycle.WaitForRegistrationAsync(CancellationToken.None);
        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeLessThan(500,
            "gate should be completed after second reconnect");
        // TODO [WARNING]: This test only verifies the final state (gate completed) after
        // HandleReconnectedAsync finishes. It does not verify that the gate is actually in an
        // *incomplete* state during the window between reset and re-registration completion.
        // A concurrent WaitForRegistrationAsync interleaved during that window should block,
        // but that ordering invariant is not asserted here. Add an interleaved test that
        // holds re-registration open, starts a WaitForRegistrationAsync concurrently, and
        // verifies it blocks until registration completes. (Test Quality Review)
    }

    // TODO [WARNING]: No test covers the `ct` cancellation contract for AgentConnectionLifecycle.
    // WaitWithTimeoutAsync accepts `ct` but does not include it in Task.WhenAny, so cancelling
    // `ct` does not promptly unblock the wait. A test that: (1) holds the gate open,
    // (2) calls WaitForRegistrationAsync(cts.Token), (3) cancels cts, and (4) asserts the
    // call returns promptly would both cover this contract and reveal the production gap.
    // Without this test the dead `ct` parameter goes undetected. (Test Quality Review)

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
        var buffer = new CriticalMessageBuffer();
        var signalRPipeline = ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger);
        var signalRReporter = new SignalRCompletionReporter(initialManager, signalRPipeline, buffer, mockLogger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(appStoppingToken);
        if (stopApplication is not null)
            lifetimeMock.Setup(l => l.StopApplication()).Callback(stopApplication);

        var lifecycle = new AgentConnectionLifecycle(
            initialManager,
            factory,
            signalRReporter,
            slotManager,
            new AgentId("gate-agent"),
            lifetimeMock.Object,
            mockLogger);

        return (lifecycle, initialManager, factory);
    }
}
