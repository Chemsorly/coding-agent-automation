using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for the initial-connect retry loop behind <see cref="AgentConnectionLifecycle.ConnectAndRunAsync"/>
/// (attempt cap, backoff, cancellation) and for the deregistration in
/// <see cref="AgentConnectionLifecycle.ShutdownAsync"/>.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentConnectionLifecycleConnectShutdownTests
{
    private const string TestAgentId = "test-agent";

    // ── Initial-connect retry ────────────────────────────────────────────

    [Fact]
    public async Task ConnectAndRunAsync_StartFailsTwiceThenSucceeds_RetriesWithBackoffAndRegisters()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        var delays = RecordDelays(lifecycle);
        manager.StartFunc = _ => manager.StartCallCount <= 2
            ? Task.FromException(new HttpRequestException("404 during API startup"))
            : Task.CompletedTask;

        await lifecycle.ConnectAndRunAsync(CancellationToken.None);

        manager.StartCallCount.Should().Be(3, "two transient failures are retried and the third attempt connects");
        delays.Should().Equal(new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4) },
            "the backoff starts at 2 s and doubles");
        connection.Invocations.Should().ContainSingle(i => i.MethodName == HubMethodNames.RegisterAgent,
            "the agent registers once after it connects");
    }

    [Fact]
    public async Task ConnectAndRunAsync_StartAlwaysFails_GivesUpAfterTenAttemptsAndRethrows()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        var delays = RecordDelays(lifecycle);
        manager.StartException = new HttpRequestException("connection refused");

        var act = () => lifecycle.ConnectAndRunAsync(CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>().WithMessage("connection refused");
        manager.StartCallCount.Should().Be(10, "the initial connect is capped at 10 attempts");
        delays.Should().Equal(
            new[] { 2, 4, 8, 16, 30, 30, 30, 30, 30 }.Select(s => TimeSpan.FromSeconds(s)),
            "the backoff doubles from 2 s, is capped at 30 s, and there is no wait after the last attempt");
        connection.Invocations.Should().BeEmpty("an agent that never connected must not register");
    }

    [Fact]
    public async Task ConnectAndRunAsync_StartTimesOutWithoutStop_RetriesAndRegisters()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        var delays = RecordDelays(lifecycle);
        // A cancelled task whose token is not the stopping token, as an HttpClient timeout produces.
        manager.StartFunc = _ => manager.StartCallCount == 1
            ? Task.FromCanceled(new CancellationToken(canceled: true))
            : Task.CompletedTask;

        await lifecycle.ConnectAndRunAsync(CancellationToken.None);

        manager.StartCallCount.Should().Be(2, "a cancellation that is not a stop request is a transient failure");
        delays.Should().Equal(new[] { TimeSpan.FromSeconds(2) });
        connection.Invocations.Should().ContainSingle(i => i.MethodName == HubMethodNames.RegisterAgent);
    }

    [Fact]
    public async Task ConnectAndRunAsync_StoppedDuringRetryDelay_ReturnsWithoutRegistering()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        manager.StartException = new HttpRequestException("connection refused");
        using var cts = new CancellationTokenSource();
        var delays = new List<TimeSpan>();
        // TODO: This test relies on the broadness of `catch (OperationCanceledException) { return false; }` around
        // the delay — any OCE (not just one from stoppingToken) exits the loop. If a future change adds a
        // `when (stoppingToken.IsCancellationRequested)` filter to that catch (analogous to the outer catch),
        // this test would pass but the loop would keep running. Review if the catch filter is ever tightened.
        lifecycle.ConnectRetryDelayFunc = (delay, ct) =>
        {
            delays.Add(delay);
            cts.Cancel(); // the host stops while the agent waits for the next attempt
            return Task.FromCanceled(ct);
        };

        var act = () => lifecycle.ConnectAndRunAsync(cts.Token);

        await act.Should().NotThrowAsync("a stop during the retry wait ends the connect loop quietly");
        manager.StartCallCount.Should().Be(1, "no further attempt is made after the stop request");
        delays.Should().Equal(new[] { TimeSpan.FromSeconds(2) });
        connection.Invocations.Should().BeEmpty("a stopped agent must not register");
    }

    [Fact]
    public async Task ConnectRetryDelayFunc_Default_WaitsUntilCancelled()
    {
        var (lifecycle, _, _) = CreateLifecycle();
        using var cts = new CancellationTokenSource();

        var delay = lifecycle.ConnectRetryDelayFunc(TimeSpan.FromSeconds(30), cts.Token);

        // TODO: `delay.IsCompleted.Should().BeFalse(...)` is a racy timing assertion — a sufficiently loaded
        // CI machine that executes 30 real seconds between these two lines would produce a false failure.
        // A more robust alternative: await the task with a very short timeout and assert that it times out,
        // rather than sampling IsCompleted.
        delay.IsCompleted.Should().BeFalse("the production wait lasts the requested time");
        await cts.CancelAsync();
        var act = () => delay;
        await act.Should().ThrowAsync<OperationCanceledException>("the production wait ends when the agent stops");
    }

    // ── ShutdownAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task ShutdownAsync_WhenConnected_DeregistersAgentAndStopsConnection()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        manager.IsConnected = true;

        await lifecycle.ShutdownAsync();

        var invocation = connection.Invocations.Should().ContainSingle().Which;
        invocation.MethodName.Should().Be(HubMethodNames.DeregisterAgent);
        invocation.Args.Should().ContainSingle().Which.Should().Be(TestAgentId);
        manager.StopCallCount.Should().Be(1, "the connection is closed after deregistering");
    }

    [Fact]
    public async Task ShutdownAsync_WhenDeregisterThrows_StillStopsConnection()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();
        manager.IsConnected = true;
        connection.InvokeException = new InvalidOperationException("hub unavailable");

        var act = () => lifecycle.ShutdownAsync();

        await act.Should().NotThrowAsync("a failed deregistration must not abort shutdown");
        connection.Invocations.Should().ContainSingle(i => i.MethodName == HubMethodNames.DeregisterAgent);
        manager.StopCallCount.Should().Be(1, "the connection is still closed after a failed deregistration");
    }

    [Fact]
    public async Task ShutdownAsync_WhenNotConnected_SkipsDeregisterAndStopsConnection()
    {
        var (lifecycle, manager, connection) = CreateLifecycle();

        await lifecycle.ShutdownAsync();

        connection.Invocations.Should().BeEmpty("there is no live connection to deregister over");
        manager.StopCallCount.Should().Be(1, "the connection is closed even when it was not connected");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static (AgentConnectionLifecycle Lifecycle, FakeHubConnectionManager Manager,
        RecordingHubConnection Connection) CreateLifecycle()
    {
        var connection = new RecordingHubConnection();
        var manager = new FakeHubConnectionManager(connection);
        var factory = new FakeHubConnectionManagerFactory(() => new FakeHubConnectionManager());
        var lifetimeMock = new Mock<IHostApplicationLifetime>();
        lifetimeMock.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var lifecycle = new AgentConnectionLifecycle(
            manager,
            factory,
            new AgentId(TestAgentId),
            lifetimeMock.Object,
            new Mock<Serilog.ILogger>().Object,
            new AgentRuntimeOptions
            {
                IsChatMode = true,
                ChatSessionId = "session-1",
                ChatModel = "auto",
                AgentLabels = "kiro"
            });

        // Chat mode returns from ConnectAndRunAsync once the chat has ended; ending it up front
        // makes ConnectAndRunAsync return right after RegisterAgent.
        lifecycle.SignalChatEnd();
        return (lifecycle, manager, connection);
    }

    /// <summary>Replaces the retry wait with one that records the requested delay and returns at once.</summary>
    private static List<TimeSpan> RecordDelays(AgentConnectionLifecycle lifecycle)
    {
        var delays = new List<TimeSpan>();
        lifecycle.ConnectRetryDelayFunc = (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        };
        return delays;
    }
}
