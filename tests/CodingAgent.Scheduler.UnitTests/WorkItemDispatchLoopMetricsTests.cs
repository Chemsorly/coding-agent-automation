using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Scheduler.Services;
using Moq;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// MeterListener-based unit tests verifying that
/// <see cref="WorkDistributionTelemetry.DispatcherPollCount"/> is incremented on every
/// <see cref="WorkItemDispatchLoop.PollAndDispatchAsync"/> call.
///
/// Placed in <c>[Collection("Metrics")]</c> to serialize against other MeterListener tests
/// in this assembly, preventing cross-talk through the process-global
/// <see cref="WorkDistributionTelemetry.Meter"/> singleton.
/// </summary>
[Collection("Metrics")]
public sealed class WorkItemDispatchLoopMetricsTests
{
    private readonly Mock<IPipelineApiWorkItemClient> _mockClient;
    private readonly Mock<ILeaderGate> _mockLeaderGate;
    private readonly Mock<ILogger> _mockLogger;

    public WorkItemDispatchLoopMetricsTests()
    {
        _mockClient = new Mock<IPipelineApiWorkItemClient>();
        _mockLeaderGate = new Mock<ILeaderGate>();
        _mockLogger = new Mock<ILogger>();
        _mockLogger.Setup(l => l.ForContext<WorkItemDispatchLoop>())
            .Returns(_mockLogger.Object);
        _mockLogger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);
    }

    private WorkItemDispatchLoop CreatePoller() =>
        new WorkItemDispatchLoop(
            _mockClient.Object,
            _mockLeaderGate.Object,
            _mockLogger.Object,
            rateLimitPerSecond: 100);

    private static PendingWorkItemDto MakeItem(Guid? id = null, string agentSelector = "kiro") =>
        new PendingWorkItemDto
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

    /// <summary>
    /// Creates a per-test MeterListener that captures Add() calls to
    /// <c>workdistribution.dispatcher_polls</c> during the gated region.
    /// Returns the counter bag and a disposable listener. Warm-up Add(0) fires
    /// before the gate to ensure the static instrument is subscribed.
    /// </summary>
    private static (ConcurrentBag<long> measurements, MeterListener listener, Action<bool> setGate)
        CreateGatedListener()
    {
        var measurements = new ConcurrentBag<long>();
        var listener = new MeterListener();
        // TODO: gateOpen is a plain bool captured by closure and read from the MeterListener
        // callback (which fires synchronously on the Add-calling thread here, but that is not
        // guaranteed by the API contract). If DispatcherPollCount.Add is ever called from a
        // background thread, or if CreateGatedListener is reused in a concurrent scenario, the
        // unsynchronised bool read/write creates a data race. Replace with `volatile bool` or
        // an `int` field manipulated via Interlocked to make the helper safe for future use.
        var gateOpen = false;

        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.dispatcher_polls")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (gateOpen && instrument.Name == "workdistribution.dispatcher_polls")
                measurements.Add(value);
        });
        listener.Start();

        // Warm-up: DispatcherPollCount is a static field initialized at type load, which
        // may predate listener.Start(). The Start() call triggers a retroactive
        // InstrumentPublished callback for already-existing instruments (see
        // brain: technology/opentelemetry.md — "Per-test MeterListener.Start() ordering"),
        // so subscription is established. The warm-up Add(0) confirms subscription is live
        // without polluting the measurement bag (gate is still closed).
        // TODO: The warm-up Add(0) does not assert that the subscription was actually
        // established — if InstrumentPublished silently fails to fire (e.g., wrong meter
        // name or type-load ordering issue), this call completes without error and subsequent
        // tests would pass vacuously because real Add(1) calls would also be silently dropped.
        // Consider asserting that at least one instrument was enabled after Start(), or use
        // a dedicated signal (e.g., a ManualResetEventSlim set inside InstrumentPublished)
        // to confirm the subscription is live before opening the gate.
        WorkDistributionTelemetry.DispatcherPollCount.Add(0);

        return (measurements, listener, open => gateOpen = open);
    }

    // ── DispatcherPollCount — empty-queue path ───────────────────────────

    /// <summary>
    /// When the pending queue is empty, <c>PollAndDispatchAsync</c> takes the early-return
    /// path. The poll counter must still increment exactly once.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenQueueEmpty_IncrementsDispatcherPollCount()
    {
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());

        var (measurements, listener, setGate) = CreateGatedListener();
        using (listener)
        {
            setGate(true);
            await CreatePoller().PollAndDispatchAsync(CancellationToken.None);
            setGate(false);
        }

        measurements.Should().ContainSingle("the counter must increment once on the empty-queue path")
            .Which.Should().Be(1L);
    }

    // ── DispatcherPollCount — dispatch path ──────────────────────────────

    /// <summary>
    /// When items are present and dispatched, <c>PollAndDispatchAsync</c> takes the
    /// full dispatch loop path. The poll counter must increment exactly once.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_WhenItemsDispatched_IncrementsDispatcherPollCount()
    {
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([MakeItem()]);
        _mockClient
            .Setup(c => c.DispatchPendingAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DispatchPendingResult.Dispatched);
        // TODO: _mockLeaderGate has no explicit setup here. The test silently relies on the
        // fact that PollAndDispatchAsync does not check ILeaderGate (the leader check lives
        // only in the outer ExecuteAsync loop). If PollAndDispatchAsync ever gains an internal
        // leader check, Moq will return default(bool)/false and this test would silently pass
        // with wrong behavior rather than failing loudly. Add explicit setup for ILeaderGate
        // if the leader-gate contract for PollAndDispatchAsync is ever expanded.

        var (measurements, listener, setGate) = CreateGatedListener();
        using (listener)
        {
            setGate(true);
            await CreatePoller().PollAndDispatchAsync(CancellationToken.None);
            setGate(false);
        }

        measurements.Should().ContainSingle("the counter must increment once on the dispatch path")
            .Which.Should().Be(1L);
    }

    // ── DispatcherPollCount — exception-return path ──────────────────────
    // TODO: Add a test covering the GetPendingAsync-throws path. DispatcherPollCount.Add(1)
    // is placed before the try/catch around GetPendingAsync, so the counter should increment
    // even when GetPendingAsync throws a non-cancellation exception. This path is correct by
    // construction today, but there is no test asserting it. A future refactor that moves
    // Add(1) inside the try block would silently break the requirement (counter would no
    // longer increment on the exception path) while all existing tests continued to pass.
    // See: WorkItemDispatchLoop.cs — the try/catch around _workItemClient.GetPendingAsync.

    // ── DispatcherPollCount — monotonic multi-invocation ─────────────────

    /// <summary>
    /// Each call to <c>PollAndDispatchAsync</c> must increment the counter exactly once,
    /// regardless of queue state. Three sequential calls must produce three Add(1) calls.
    /// </summary>
    [Fact]
    public async Task PollAndDispatch_MultipleInvocations_CounterIncrementsEachTime()
    {
        _mockClient
            .Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());

        var (measurements, listener, setGate) = CreateGatedListener();
        var poller = CreatePoller();
        using (listener)
        {
            setGate(true);
            await poller.PollAndDispatchAsync(CancellationToken.None);
            await poller.PollAndDispatchAsync(CancellationToken.None);
            await poller.PollAndDispatchAsync(CancellationToken.None);
            setGate(false);
        }

        measurements.Should().HaveCount(3, "each of the three PollAndDispatchAsync calls must add 1 to the counter");
        measurements.Should().AllSatisfy(m => m.Should().Be(1L), "each increment must be exactly +1");
    }
}
