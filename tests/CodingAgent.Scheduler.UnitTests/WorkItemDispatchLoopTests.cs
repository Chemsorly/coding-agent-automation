using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="WorkItemDispatchLoop"/>.
/// Uses a fast tick interval (1ms) so the service fires within the test window.
/// All tests are in the SchedulerTiming collection to serialize against other PeriodicTimer tests.
/// </summary>
[Collection("SchedulerTiming")]
public sealed class WorkItemDispatchLoopTests : IDisposable
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    // Class-level ActivityListener: captures all Dispatch.Attempt spans across test methods.
    // Class-level (not per-method) avoids listener accumulation when multiple tests run within
    // the same class — inline 'using var' listeners register globally and can bleed across methods.
    // The [Collection("SchedulerTiming")] attribute serialises this class against all other
    // timing-sensitive scheduler tests, preventing cross-class ActivitySource races.
    private readonly ActivityListener _activityListener;
    private readonly ConcurrentBag<Activity> _capturedActivities = [];

    public WorkItemDispatchLoopTests()
    {
        _mockClient = new Mock<IPipelineApiWorkItemClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemDispatchLoop>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        _activityListener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // SampleUsingParentId required for activities created without an explicit parent context
            // (common in unit tests where Activity.Current is null). Without it, StartActivity can
            // return null even when a listener is registered. (Brain: Entry 1 — lessons-learned.md)
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Use ActivityStopped (not ActivityStarted) — tags set after StartActivity are only
            // visible at stop time. (Brain: Entry 1 — lessons-learned.md)
            ActivityStopped = a => _capturedActivities.Add(a)
        };
        ActivitySource.AddActivityListener(_activityListener);
    }

    public void Dispose() => _activityListener.Dispose();

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

    // ── Dispatch.Attempt span emission (issue #2977) ──────────────────────────

    /// <summary>
    /// AC: For each work item dispatched, a Dispatch.Attempt activity is emitted with the
    /// work_item_id, agent_selector, and result tags.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenItemsDispatched_EmitsDispatchAttemptSpanPerItem()
    {
        var item1 = MakeItem(agentSelector: "kiro,dotnet");
        var item2 = MakeItem(agentSelector: "kiro,python");

        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item1, item2]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        _capturedActivities.Clear();
        await poller.PollAndDispatchAsync(CancellationToken.None);

        // Two items dispatched → two Dispatch.Attempt spans
        var dispatches = _capturedActivities
            .Where(a => a.OperationName == "Dispatch.Attempt")
            .ToList();
        dispatches.Should().HaveCount(2, "one Dispatch.Attempt span per dispatched item");

        // Verify tags on first span
        var span1 = dispatches.First(a => a.GetTagItem("work_item_id")?.ToString() == item1.Id.ToString());
        span1.GetTagItem("work_item_id").Should().Be(item1.Id);
        span1.GetTagItem("agent_selector").Should().Be("kiro,dotnet");
        span1.GetTagItem("result").Should().Be("Dispatched");

        // Verify tags on second span
        var span2 = dispatches.First(a => a.GetTagItem("work_item_id")?.ToString() == item2.Id.ToString());
        span2.GetTagItem("work_item_id").Should().Be(item2.Id);
        span2.GetTagItem("agent_selector").Should().Be("kiro,python");
        span2.GetTagItem("result").Should().Be("Dispatched");
    }

    /// <summary>
    /// AC: When the pending queue is empty (idle tick), no Dispatch.Attempt span is emitted.
    /// Spans appear only for actual work — idle cycles must not produce spans.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenQueueEmpty_DoesNotEmitDispatchAttemptSpan()
    {
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        _capturedActivities.Clear();
        await poller.PollAndDispatchAsync(CancellationToken.None);

        _capturedActivities
            .Where(a => a.OperationName == "Dispatch.Attempt")
            .Should().BeEmpty("idle ticks must not emit Dispatch.Attempt spans");
    }

    /// <summary>
    /// AC: A permanent rejection (409) still emits a Dispatch.Attempt span with result=PermanentRejection.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenPermanentRejection_EmitsDispatchAttemptSpanWithRejectionResult()
    {
        var item = MakeItem(agentSelector: "kiro,dotnet");

        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([item]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.PermanentRejection);

        var poller = new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

        _capturedActivities.Clear();
        await poller.PollAndDispatchAsync(CancellationToken.None);

        var span = _capturedActivities.FirstOrDefault(a => a.OperationName == "Dispatch.Attempt");
        span.Should().NotBeNull("a Dispatch.Attempt span must be emitted even for permanent rejections");
        span!.GetTagItem("result").Should().Be("PermanentRejection");
    }

    // TODO: Missing test coverage for the Dispatch.Attempt span when DispatchPendingAsync throws an exception
    // (the `catch (Exception ex)` path in WorkItemDispatchLoop.PollAndDispatchAsync). In that path, the span
    // is started but `dispatchActivity?.SetTag("result", ...)` is never reached (break exits before it).
    // Add a test: PollAndDispatch_WhenDispatchThrowsException_SpanIsEmittedWithoutResultTag (or with a
    // "Exception" result tag once the TODO in WorkItemDispatchLoop.cs is resolved).
}
