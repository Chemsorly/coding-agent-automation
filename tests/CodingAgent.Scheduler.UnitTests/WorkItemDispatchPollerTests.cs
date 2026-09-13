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
/// Unit tests for <see cref="WorkItemDispatchPoller"/>.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// All tests are in the SchedulerTiming collection to serialize against other PeriodicTimer tests.
/// </summary>
[Collection("SchedulerTiming")]
public sealed class WorkItemDispatchPollerTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemDispatchPollerTests()
    {
        _mockClient = new Mock<IPipelineApiWorkItemClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemDispatchPoller>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private WorkItemDispatchPoller CreatePoller()
        => new WorkItemDispatchPoller(
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

    private static async Task RunPollerForDurationAsync(WorkItemDispatchPoller poller, TimeSpan duration)
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
        // TODO [WARNING]: this test runs the BackgroundService loop for 500ms with a 1ms tick, so the
        // mock fires hundreds of times. The assertion is Times.AtLeastOnce(), which passes even if the
        // first 99 cycles silently dropped the dispatch. For a more precise "leader dispatches the item"
        // contract, call PollAndDispatchAsync directly and assert Times.Once(), as the per-selector and
        // transient tests do.
        var itemId = Guid.NewGuid();
        _mockLeaderGate.SetupGet(g => g.IsLeader).Returns(true);
        // Set up mock BEFORE StartAsync so the immediate first tick sees a configured mock.
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem(itemId)]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(500));

        _mockClient.Verify(c => c.DispatchPendingAsync(itemId, It.IsAny<CancellationToken>()),
            Times.AtLeastOnce(), "leader poller must dispatch the pending item");
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
        // TODO [WARNING]: this test runs for 2000ms wall-clock time (1ms tick × 2000ms), making it
        // one of the slowest unit tests in this suite for no additional coverage benefit — the mock
        // returns [] every cycle, so it only proves GetPendingAsync was called at least once. The same
        // assertion can be achieved in microseconds by calling PollAndDispatchAsync directly with a
        // null-gate poller instance.
        // null gate = dev / single-replica mode
        // Set up mock before constructing poller to avoid unconfigured mock on first poll.
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        var poller = new WorkItemDispatchPoller(
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
        // TODO [WARNING]: this test only verifies that all three items are eventually dispatched
        // (Times.AtLeastOnce), which would pass equally well for a parallel-dispatch implementation.
        // The word "sequentially" in the test name is not validated. A meaningful sequentiality test
        // would record the call order (e.g. via a callback that appends to a list) and assert the
        // expected ordering, or verify that id2 was not dispatched before id1 completed.
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

        await RunPollerForDurationAsync(CreatePoller(), TimeSpan.FromMilliseconds(500));

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
        var poller = new WorkItemDispatchPoller(
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

        var poller = new WorkItemDispatchPoller(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        await poller.PollAndDispatchAsync(CancellationToken.None);

        // id1 dispatched (transient failure)
        _mockClient.Verify(c => c.DispatchPendingAsync(id1, It.IsAny<CancellationToken>()), Times.Once());
        // id2 must NOT be dispatched — cycle aborted
        _mockClient.Verify(c => c.DispatchPendingAsync(id2, It.IsAny<CancellationToken>()), Times.Never());
        // Verify any Warning-level call was made. Serilog uses generic template overloads;
        // the actual call is ILogger.Warning<Guid>(messageTemplate, workItemId).
        // TODO [WARNING]: this logger verify is redundant — the behavioral assertions above (id2 never
        // dispatched) already prove the cycle was aborted. The Warning(string, Guid) overload match
        // is fragile: if the log arg type changes or a different overload is used, the verify silently
        // passes vacuously (loose mock returns default for unmatched calls), masking a missing warning.
        // Consider removing this verify and relying solely on the behavioral assertions.
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
        var poller = new WorkItemDispatchPoller(
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

        var poller = new WorkItemDispatchPoller(
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
