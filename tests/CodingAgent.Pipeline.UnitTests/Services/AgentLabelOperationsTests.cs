using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AgentLabelOperations"/>.
/// </summary>
public class AgentLabelOperationsTests
{
    [Fact]
    public async Task SwapAsync_AddsTarget_ThenRemovesAllOtherLabels()
    {
        var removed = new List<string>();
        var added = new List<string>();
        var callOrder = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); callOrder.Add($"remove:{label}"); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); callOrder.Add($"add:{label}"); return Task.CompletedTask; },
            AgentLabels.InProgress,
            CancellationToken.None);

        removed.Should().NotContain(AgentLabels.InProgress);
        removed.Should().HaveCount(AgentLabels.All.Count - 1);
        added.Should().ContainSingle().Which.Should().Be(AgentLabels.InProgress);

        // Add happens before any removes (crash-safe ordering)
        callOrder.First().Should().Be($"add:{AgentLabels.InProgress}");
    }

    [Fact]
    public async Task SwapAsync_WhenNewLabelIsEmpty_RemovesAllWithoutAdding()
    {
        var removed = new List<string>();
        var added = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); return Task.CompletedTask; },
            string.Empty,
            CancellationToken.None);

        removed.Should().HaveCount(AgentLabels.All.Count);
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task SwapAsync_WhenNewLabelIsNull_RemovesAllWithoutAdding()
    {
        var removed = new List<string>();
        var added = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => { added.Add(label); return Task.CompletedTask; },
            null!,
            CancellationToken.None);

        removed.Should().HaveCount(AgentLabels.All.Count);
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task SwapAsync_SkipsTargetLabelInRemoveLoop()
    {
        var removed = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            (label, ct) => Task.CompletedTask,
            AgentLabels.Error,
            CancellationToken.None);

        removed.Should().NotContain(AgentLabels.Error);
    }

    [Fact]
    public async Task RemoveAllAsync_RemovesEveryLabelInAgentLabelsAll()
    {
        var removed = new List<string>();

        await AgentLabelOperations.RemoveAllAsync(
            (label, ct) => { removed.Add(label); return Task.CompletedTask; },
            CancellationToken.None);

        removed.Should().BeEquivalentTo(AgentLabels.All);
    }

    [Fact]
    public async Task SwapAsync_PropagatesCancellationToken()
    {
        using var cts = new CancellationTokenSource();
        var receivedTokens = new List<CancellationToken>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) => { receivedTokens.Add(ct); return Task.CompletedTask; },
            (label, ct) => { receivedTokens.Add(ct); return Task.CompletedTask; },
            AgentLabels.Done,
            cts.Token);

        receivedTokens.Should().AllSatisfy(t => t.Should().Be(cts.Token));
    }

    // ── removeLabel failure path ──────────────────────────────────────────

    /// <summary>
    /// AC-a: When removeLabel throws, a Warning is logged naming the specific label that failed.
    /// Uses an injected ILogger mock to avoid the static Log.Logger capture issue.
    /// </summary>
    [Fact]
    public async Task SwapAsync_WhenRemoveLabelThrows_LogsWarningNamingFailingLabel()
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var callCount = 0;

        await AgentLabelOperations.SwapAsync(
            (label, ct) =>
            {
                callCount++;
                if (callCount == 2) throw new InvalidOperationException("API error");
                return Task.CompletedTask;
            },
            (label, ct) => Task.CompletedTask,
            AgentLabels.InProgress,
            CancellationToken.None,
            identifier: "GH-99",
            logger: mockLogger.Object);

        // Must have logged at Warning level with an Exception, a message template, and the label name
        mockLogger.Verify(
            l => l.Warning(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<int>(),        // attempt number
                It.IsAny<string>(),     // label
                It.IsAny<string>()),    // identifier
            Times.AtLeastOnce);
    }

    /// <summary>
    /// AC-b: When removeLabel throws mid-loop, the loop continues and all remaining labels
    /// are still attempted.
    /// The second remove call throws unconditionally (exhausting all retries), but the
    /// outer foreach must continue to attempt the remaining 8 labels.
    /// </summary>
    [Fact]
    public async Task SwapAsync_WhenRemoveLabelThrowsMidLoop_ContinuesToAttemptRemainingLabels()
    {
        var attempted = new List<string>();

        await AgentLabelOperations.SwapAsync(
            (label, ct) =>
            {
                attempted.Add(label);
                // Throw on every attempt for agent:error (second distinct label in the loop
                // when newLabel=InProgress) so all 3 retries exhaust before the loop continues.
                if (label == AgentLabels.Error) throw new InvalidOperationException("transient error");
                return Task.CompletedTask;
            },
            (label, ct) => Task.CompletedTask,
            AgentLabels.InProgress,     // skipped in remove loop
            CancellationToken.None,
            logger: Mock.Of<Serilog.ILogger>());

        // All distinct labels except newLabel (InProgress) should have been attempted
        var distinctAttempted = attempted.Distinct().ToList();
        distinctAttempted.Should().HaveCount(AgentLabels.All.Count - 1);
        distinctAttempted.Should().NotContain(AgentLabels.InProgress);
        // Specifically, the labels after the failing one (Error) must also appear
        distinctAttempted.Should().Contain(AgentLabels.NeedsRefinement);
        distinctAttempted.Should().Contain(AgentLabels.Done);
    }

    /// <summary>
    /// Cancellation guard: OperationCanceledException thrown by removeLabel must propagate
    /// immediately and must not be swallowed by the new catch clauses in the retry loop.
    /// </summary>
    [Fact]
    public async Task SwapAsync_WhenRemoveLabelThrowsOce_PropagatesImmediately()
    {
        var callCount = 0;

        var act = () => AgentLabelOperations.SwapAsync(
            (label, ct) =>
            {
                callCount++;
                if (callCount == 1) throw new OperationCanceledException();
                return Task.CompletedTask;
            },
            (label, ct) => Task.CompletedTask,
            AgentLabels.InProgress,
            CancellationToken.None,
            logger: Mock.Of<Serilog.ILogger>());

        await act.Should().ThrowAsync<OperationCanceledException>();
        callCount.Should().Be(1); // loop aborted — remaining labels not attempted
    }

    // ── counter telemetry ─────────────────────────────────────────────────

    /// <summary>
    /// AC: When removeLabel exhausts all retries on the throwOnRemoveExhaustion=false path,
    /// the label_swap_remove_exhausted_total counter must be incremented exactly once
    /// for the failing label, with correct label and identifier tags.
    /// Uses an injected counter (TestMeterFactory) for isolation — no [Collection("Metrics")]
    /// needed because the injected counter does not touch PipelineTelemetry.Meter.
    /// </summary>
    [Fact]
    public async Task SwapAsync_WhenRemoveLabelExhaustsRetries_IncrementsLabelSwapRemoveExhaustedCounter()
    {
        // factory is the shared observer — meter must be created from it so that the
        // MetricCollector (also subscribed to factory) can capture Add() calls on the
        // injected counter. Without this linkage the collector has no instruments to observe.
        using var factory = new TestMeterFactory();
        using var meter = factory.Create(new System.Diagnostics.Metrics.MeterOptions(PipelineTelemetry.SourceName));
        var counter = meter.CreateCounter<long>("label_swap_remove_exhausted_total");
        using var collector = new MetricCollector<long>(factory, PipelineTelemetry.SourceName, "label_swap_remove_exhausted_total");

        await AgentLabelOperations.SwapAsync(
            (label, ct) =>
            {
                // AgentLabels.Error throws unconditionally, exhausting all 3 retry attempts.
                if (label == AgentLabels.Error) throw new InvalidOperationException("transient");
                return Task.CompletedTask;
            },
            (label, ct) => Task.CompletedTask,
            AgentLabels.InProgress,          // skipped in the remove loop
            CancellationToken.None,
            identifier: "GH-42",
            logger: Mock.Of<Serilog.ILogger>(),
            exhaustionCounter: counter);     // injected — captured by collector via factory

        var snapshot = collector.GetMeasurementSnapshot();
        // AgentLabels.Error is the only label that exhausts retries — expect exactly one measurement.
        // TODO: This test does not verify that labels other than AgentLabels.Error do NOT produce
        // spurious counter increments. The removeLabel delegate returns Task.CompletedTask for all
        // other labels, so a bug that inverted the label check would still make this assertion pass.
        // Consider adding an assertion that snapshot count equals 1 (i.e. no other labels fire),
        // or parameterising the test to confirm only the explicitly-thrown label appears.
        snapshot.Should().ContainSingle(m =>
            m.Value == 1 &&
            m.Tags.Contains(new KeyValuePair<string, object?>("label", AgentLabels.Error)) &&
            m.Tags.Contains(new KeyValuePair<string, object?>("identifier", "GH-42")));
    }

    // TODO: Missing negative-case test — verify the counter is NOT incremented when
    // throwOnRemoveExhaustion=true and retries are exhausted. The counter must only fire
    // on the else branch (swallowed path). A regression that moves counter.Add() above the
    // if/else split would go undetected without this test. Suggested test signature:
    // SwapAsync_WhenRemoveLabelExhaustsRetries_AndThrowOnRemoveExhaustionIsTrue_DoesNotIncrementCounter

    // TODO: Missing multi-label-exhaustion test — verify the counter fires once per failing label
    // when two or more labels exhaust retries in the same SwapAsync call. The ContainSingle
    // assertion in the existing test only validates the single-label case. A defect that
    // guarded the counter.Add with a "fire-only-once" flag would not be caught by the current tests.
    // Suggested test: supply a removeLabel delegate that throws for two labels (e.g. AgentLabels.Error
    // and AgentLabels.Done) and assert the collector snapshot has exactly two measurements.
}
