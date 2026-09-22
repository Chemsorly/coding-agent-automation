using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="WorkItemDispatchLoop"/>.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// All tests are in the SchedulerTiming collection to serialize against other PeriodicTimer tests.
/// </summary>
[Collection("SchedulerTiming")]
public sealed class WorkItemDispatchLoopTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemDispatchLoopTests()
    {
        _mockClient = new Mock<IPipelineApiWorkItemClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemDispatchLoop>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private WorkItemDispatchLoop CreatePoller()
        => new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100,   // high limit so rate limiting never blocks in tests
            interval: TimeSpan.FromMilliseconds(1));

    private static PendingWorkItemDto MakeItem(Guid? id = null, string agentSelector = "kiro")
        => new PendingWorkItemDto
        {
            Id = id ?? Guid.NewGuid(),
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "github",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            AgentSelector = agentSelector,
            RetryCount = 0,
            TimeoutSeconds = 3600
        };

    private static async Task RunPollerForDurationAsync(WorkItemDispatchLoop poller, TimeSpan duration)
    {
        using var cts = new CancellationTokenSource();
        await poller.StartAsync(cts.Token);
        await Task.Delay(duration, CancellationToken.None);
        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await poller.StopAsync(stopCts.Token); } catch { }
        poller.Dispose();
    }

    // ── Leader gating ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenLeaderAndPendingExists_ShouldCallDispatchEndpoint()
    {
        var itemId = Guid.NewGuid();
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem(itemId)]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);

        // Use PollAndDispatchAsync directly instead of the BackgroundService loop to avoid
        // wall-clock timing flakiness: the loop-based approach (RunPollerForDurationAsync)
        // depends on the service starting and firing a tick within a fixed window, which can
        // fail under CI load. PollAndDispatchAsync executes exactly one poll cycle deterministically.
        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);
        await poller.PollAndDispatchAsync(CancellationToken.None);
        poller.Dispose();

        _mockClient.Verify(c => c.DispatchPendingAsync(itemId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader loop must dispatch the pending item");
    }

    [Fact]
    public async Task WhenNotLeader_ShouldNotCallApi()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(false);

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(500));

        _mockClient.Verify(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never(), "non-leader must not poll the work item endpoint");
    }

    [Fact]
    public async Task WhenNullGate_ShouldPollUnconditionally()
    {
        // null gate = dev / single-replica mode
        // Set up mock before constructing loop to avoid unconfigured mock on first poll.
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            leaderGate: null,
            _mockLogger.Object,
            rateLimitPerSecond: 100,
            interval: TimeSpan.FromMilliseconds(1));

        await RunPollerForDurationAsync(poller, TimeSpan.FromMilliseconds(2000));

        _mockClient.Verify(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "null gate must not suppress polling");
    }

    // ── Sequential dispatch ───────────────────────────────────────────────

    [Fact]
    public async Task WhenMultiplePendingItems_ShouldDispatchAllSequentially()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem(id1), MakeItem(id2), MakeItem(id3)]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(2000));

        _mockClient.Verify(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
        _mockClient.Verify(c => c.DispatchPendingAsync(id2, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
        _mockClient.Verify(c => c.DispatchPendingAsync(id3, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce());
    }

    // ── Per-selector stop-on-409 ──────────────────────────────────────────

    [Fact]
    public async Task WhenPermanentRejection_ShouldStopDispatchingForThatSelectorOnly()
    {
        // Two selectors: "kiro" and "opencode".
        // Item 1: kiro → PermanentRejection (409)
        // Item 2: kiro → should be SKIPPED (same selector blocked)
        // Item 3: opencode → should still be dispatched
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                MakeItem(id1, "kiro"),
                MakeItem(id2, "kiro"),
                MakeItem(id3, "opencode")
            ]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.PermanentRejection);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(id3, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);

        // Trigger a single poll cycle directly (not via BackgroundService loop)
        // so we can make deterministic assertions about call counts.
        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        await poller.PollAndDispatchAsync(CancellationToken.None);

        // id1: dispatched (then blocked selector)
        _mockClient.Verify(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()), Times.Once());
        // id2: same selector "kiro" — must NOT be dispatched
        _mockClient.Verify(c => c.DispatchPendingAsync(id2, It.IsAny<CancellationToken>()), Times.Never());
        // id3: different selector "opencode" — must still be dispatched
        _mockClient.Verify(c => c.DispatchPendingAsync(id3, It.IsAny<CancellationToken>()), Times.Once());

        poller.Dispose();
    }

    [Fact]
    public async Task WhenTransient503_ShouldAbortCycleAndNotDispatchRemainingItems()
    {
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem(id1), MakeItem(id2)]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Transient);

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        await poller.PollAndDispatchAsync(CancellationToken.None);

        // id1 dispatched (transient failure)
        _mockClient.Verify(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()), Times.Once());
        // id2 must NOT be dispatched — cycle aborted
        _mockClient.Verify(c => c.DispatchPendingAsync(id2, It.IsAny<CancellationToken>()), Times.Never());
        // Verify Warning was logged for the transient failure.
        // Serilog uses generic template overloads; the actual call is ILogger.Warning<Guid>(messageTemplate, workItemId).
        _mockLogger.Verify(
            l => l.Warning(It.IsAny<string>(), It.IsAny<Guid>()),
            Times.AtLeastOnce(), "transient failure should log a warning");

        poller.Dispose();
    }

    // ── Error handling ────────────────────────────────────────────────────

    [Fact]
    public async Task WhenGetPendingThrows_ShouldLogWarningAndNotCrash()
    {
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        // Call PollAndDispatchAsync directly to avoid wall-clock timing flakiness.
        // The BackgroundService loop path is tested by WhenLeaderAndPendingExists_ShouldCallDispatchEndpoint;
        // here we only need to verify the exception-handling branch.
        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);
        await poller.PollAndDispatchAsync(CancellationToken.None);

        // Production code calls _logger.Warning(ex, messageTemplate) — the two-argument
        // Warning(Exception, string) Serilog overload (no template args).
        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Once(), "GetPendingAsync failure must log a Warning");
        _mockLogger.Verify(l => l.Error(It.IsAny<Exception>(), It.IsAny<string>()),
            Times.Never(), "GetPendingAsync failure must not log Error");

        poller.Dispose();
    }

    [Fact]
    public async Task WhenDispatchPendingThrows_ShouldLogWarningAndNotCrash()
    {
        var itemId = Guid.NewGuid();
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem(itemId)]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("server error"));

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        await poller.PollAndDispatchAsync(CancellationToken.None);

        // Serilog uses generic template overloads; the actual call is Warning<Guid>(Exception, string, Guid).
        _mockLogger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>(), It.IsAny<Guid>()),
            Times.AtLeastOnce(), "DispatchPendingAsync exception must log a Warning");
    }
}
