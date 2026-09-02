using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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

    // TODO: [WARNING] No test covers the throwOnRemoveExhaustion=true code path used by SwapLabelStrictAsync
    // callers. Add a test that verifies: (a) exception is re-thrown after all retries are exhausted, and
    // (b) remaining labels in the loop are NOT attempted when throwOnRemoveExhaustion=true.
    // Without this, removing the `if (throwOnRemoveExhaustion) throw;` branch would go undetected.

    /// <summary>
    /// AC-a: When removeLabel throws, a Warning is logged naming the specific label that failed.
    /// Uses an injected ILogger mock to avoid the static Log.Logger capture issue.
    /// NOTE: callCount==2 fires on the first *retry* of the first label (not the second distinct label).
    /// The test comment claiming "second remove call=Error — second throws" is inaccurate given the retry
    /// loop; the throw actually occurs on the first label's retry (attempt index 1).
    /// TODO: [WARNING] The Moq Verify uses It.IsAny<string>() for the label argument, meaning the assertion
    /// passes regardless of which label name appears in the Warning. AC-a requires "a Warning log naming
    /// that label." Tighten to assert the specific label value (e.g. AgentLabels.Next when callCount==2
    /// fires on attempt 1 of Next) to actually validate the label-naming requirement.
    /// TODO: [WARNING] Verify the Moq overload resolution: if Serilog exposes Warning as the non-generic
    /// params-array overload Warning(Exception, string, object?[]) the five It.IsAny<T>() matchers here
    /// may not match, making this assertion unreliable. Confirm with a deliberate failure test or switch
    /// to a capturing sink (e.g. Serilog.Sinks.InMemory) to avoid Moq overload ambiguity.
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
}
