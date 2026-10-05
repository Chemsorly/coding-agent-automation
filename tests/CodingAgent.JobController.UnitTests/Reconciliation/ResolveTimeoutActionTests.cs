using AwesomeAssertions;
using CodingAgent.JobController.Reconciliation;

namespace CodingAgent.JobController.UnitTests.Reconciliation;

// TODO [WARNING]: No test exercises the wiring inside EnforceTimeoutsAsync after refactoring.
// The critical integration point `if (ResolveTimeoutAction(...) == TimeoutAction.Skip) continue;`
// is not covered: if the check were accidentally inverted to `== TimeoutAction.Enforce`, all tests
// here would still pass while enforcement logic would be completely reversed (healthy items killed,
// timed-out items left running). Add characterization tests for EnforceTimeoutsAsync that verify
// the method calls PostStatusAsync(Failed) for a timed-out item and does NOT call it for a
// within-grace item. (TestQualityReviewer review finding, issue #3340)

/// <summary>
/// Unit tests for the pure <see cref="ReconciliationLoop.ResolveTimeoutAction"/> function.
/// All tests call the internal static method directly — no I/O, no mocks, no infrastructure.
/// </summary>
/// <remarks>
/// Acceptance criteria (issue #3340):
/// - A pure function returning a typed <see cref="TimeoutAction"/> enum exists and
///   contains the boundary comparison with no I/O or dispatch inside it.
/// - A test asserts behaviour at the exact timeout boundary value.
/// </remarks>
public sealed class ResolveTimeoutActionTests
{
    // ─── WithinGrace → always Skip ────────────────────────────────────────────

    [Fact]
    public void ResolveTimeoutAction_WhenAgeResultIsWithinGrace_ReturnsSkip()
    {
        var result = ReconciliationLoop.ResolveTimeoutAction(new WithinGrace(), timeoutSeconds: 1800);

        result.Should().Be(TimeoutAction.Skip);
    }

    // ─── CanaryViolation → always Skip ───────────────────────────────────────

    [Fact]
    public void ResolveTimeoutAction_WhenAgeResultIsCanaryViolation_ReturnsSkip()
    {
        var result = ReconciliationLoop.ResolveTimeoutAction(new CanaryViolation(), timeoutSeconds: 1800);

        result.Should().Be(TimeoutAction.Skip);
    }

    // ─── Enforceable — below threshold → Skip ────────────────────────────────

    [Fact]
    public void ResolveTimeoutAction_WhenAgeIsBelowThreshold_ReturnsSkip()
    {
        // Item has been running 899 seconds; timeout is 900 seconds → not yet timed out.
        const int timeoutSeconds = 900;
        var result = ReconciliationLoop.ResolveTimeoutAction(new Enforceable(899.0), timeoutSeconds);

        result.Should().Be(TimeoutAction.Skip);
    }

    // ─── Enforceable — above threshold → Enforce ─────────────────────────────

    [Fact]
    public void ResolveTimeoutAction_WhenAgeExceedsThreshold_ReturnsEnforce()
    {
        // Item has been running 901 seconds; timeout is 900 seconds → timed out.
        const int timeoutSeconds = 900;
        var result = ReconciliationLoop.ResolveTimeoutAction(new Enforceable(901.0), timeoutSeconds);

        result.Should().Be(TimeoutAction.Enforce);
    }

    // ─── Exact boundary value ─────────────────────────────────────────────────

    /// <summary>
    /// AC (issue #3340): a test asserts the behaviour at the exact timeout boundary value.
    /// <para>
    /// Boundary semantics: <c>ageSeconds == timeoutSeconds</c> must return
    /// <see cref="TimeoutAction.Enforce"/>, not <see cref="TimeoutAction.Skip"/>.
    /// The original inline guard was <c>if (enforceable.AgeSeconds &lt; item.TimeoutSeconds) continue;</c>
    /// — strict less-than means equality triggers enforcement.
    /// </para>
    /// <para>
    /// NOTE (issue #3243): at <c>timeoutSeconds == 60</c> (the canary minimum), the canary
    /// invariant provides no additional protection because a run that cleared the canary guard
    /// (<c>age &gt;= 60</c>) has an age exactly at the enforcement boundary. This test
    /// documents that this is the intended behaviour for the boundary case.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(60)]     // canary minimum — boundary case at canary threshold
    [InlineData(900)]    // typical per-project short timeout (15 min)
    [InlineData(1800)]   // default global timeout (30 min)
    [InlineData(7200)]   // long-running project timeout (2 h)
    public void ResolveTimeoutAction_WhenAgeEqualsThreshold_ReturnsEnforce(int timeoutSeconds)
    {
        // ageSeconds == timeoutSeconds exactly — must enforce, not skip.
        var result = ReconciliationLoop.ResolveTimeoutAction(
            new Enforceable((double)timeoutSeconds), timeoutSeconds);

        result.Should().Be(TimeoutAction.Enforce,
            $"an item whose execution age equals its configured timeout ({timeoutSeconds}s) must be enforced");
    }

    // ─── Various timeout values — below and above ─────────────────────────────

    [Theory]
    [InlineData(59.9, 60, false)]      // just under the canary-min threshold → Skip
    [InlineData(60.0, 60, true)]       // at canary-min threshold → Enforce
    [InlineData(60.1, 60, true)]       // just over the canary-min threshold → Enforce
    [InlineData(1799.9, 1800, false)]  // just under the global default → Skip
    [InlineData(1800.0, 1800, true)]   // at the global default → Enforce
    [InlineData(1800.1, 1800, true)]   // just over the global default → Enforce
    public void ResolveTimeoutAction_EnforceableCases_BehavesCorrectly(
        double ageSeconds, int timeoutSeconds, bool expected)
    {
        var result = ReconciliationLoop.ResolveTimeoutAction(new Enforceable(ageSeconds), timeoutSeconds);

        var expectedAction = expected ? TimeoutAction.Enforce : TimeoutAction.Skip;
        result.Should().Be(expectedAction,
            $"age={ageSeconds}s, timeout={timeoutSeconds}s");
    }
}
