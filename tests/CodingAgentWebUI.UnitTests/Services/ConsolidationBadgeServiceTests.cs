using AwesomeAssertions;
using CodingAgentWebUI.Hub;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationBadgeService"/>.
/// Validates: Requirements 10.1, 10.2, 10.3
/// </summary>
public sealed class ConsolidationBadgeServiceTests
{
    private readonly ConsolidationBadgeService _sut = new();

    // ── Initial state ────────────────────────────────────────────────────

    [Fact]
    public void BadgeCount_Initially_IsZero()
    {
        // Validates: Requirement 10.1
        _sut.BadgeCount.Should().Be(0);
    }

    // ── IncrementBy ──────────────────────────────────────────────────────

    [Fact]
    public void IncrementBy_AddsToCurrentCount()
    {
        // Validates: Requirement 10.1
        _sut.IncrementBy(3);

        _sut.BadgeCount.Should().Be(3);
    }

    [Fact]
    public void IncrementBy_MultipleIncrements_Accumulates()
    {
        // Validates: Requirement 10.1
        _sut.IncrementBy(2);
        _sut.IncrementBy(5);

        _sut.BadgeCount.Should().Be(7);
    }

    [Fact]
    public void IncrementBy_Zero_DoesNotChangeCount()
    {
        // Validates: Requirement 10.1
        _sut.IncrementBy(3);
        _sut.IncrementBy(0);

        _sut.BadgeCount.Should().Be(3);
    }

    [Fact]
    public void IncrementBy_NegativeValue_ThrowsArgumentOutOfRangeException()
    {
        // Validates: Requirement 10.1 — guard against invalid input
        var act = () => _sut.IncrementBy(-1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Reset ────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_SetsCountToZero()
    {
        // Validates: Requirement 10.2
        _sut.IncrementBy(5);

        _sut.Reset();

        _sut.BadgeCount.Should().Be(0);
    }

    [Fact]
    public void Reset_WhenAlreadyZero_RemainsZero()
    {
        // Validates: Requirement 10.2
        _sut.Reset();

        _sut.BadgeCount.Should().Be(0);
    }

    // ── OnBadgeChanged fires on increment ────────────────────────────────

    [Fact]
    public void IncrementBy_FiresOnBadgeChanged()
    {
        // Validates: Requirement 10.1
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;

        _sut.IncrementBy(1);

        fired.Should().BeTrue();
    }

    [Fact]
    public void IncrementBy_Zero_DoesNotFireOnBadgeChanged()
    {
        // Validates: Requirement 10.1 — no event when count doesn't change
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;

        _sut.IncrementBy(0);

        fired.Should().BeFalse();
    }

    // ── OnBadgeChanged fires on reset ────────────────────────────────────

    [Fact]
    public void Reset_WhenCountNonZero_FiresOnBadgeChanged()
    {
        // Validates: Requirement 10.2
        _sut.IncrementBy(3);

        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;

        _sut.Reset();

        fired.Should().BeTrue();
    }

    [Fact]
    public void Reset_WhenCountAlreadyZero_DoesNotFireOnBadgeChanged()
    {
        // Validates: Requirement 10.2 — no event when count doesn't change
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;

        _sut.Reset();

        fired.Should().BeFalse();
    }

    // ── Badge not displayed when zero ────────────────────────────────────

    [Fact]
    public void BadgeCount_WhenZero_IndicatesBadgeShouldNotBeDisplayed()
    {
        // Validates: Requirement 10.3 — badge not displayed when count is zero
        _sut.BadgeCount.Should().Be(0);

        // The UI should check BadgeCount > 0 before displaying
        var shouldDisplay = _sut.BadgeCount > 0;
        shouldDisplay.Should().BeFalse();
    }

    [Fact]
    public void BadgeCount_WhenNonZero_IndicatesBadgeShouldBeDisplayed()
    {
        // Validates: Requirement 10.3 — badge displayed when count > 0
        _sut.IncrementBy(1);

        var shouldDisplay = _sut.BadgeCount > 0;
        shouldDisplay.Should().BeTrue();
    }

    [Fact]
    public void BadgeCount_AfterReset_IndicatesBadgeShouldNotBeDisplayed()
    {
        // Validates: Requirement 10.3 — badge hidden after reset (page visit)
        _sut.IncrementBy(5);
        _sut.Reset();

        var shouldDisplay = _sut.BadgeCount > 0;
        shouldDisplay.Should().BeFalse();
    }

    // ── HasEverBeenIncremented ─────────────────────────────────────────────

    [Fact]
    public void HasEverBeenIncremented_Initially_IsFalse()
    {
        // Validates: Req 3.9 — badge shows stale indicator when never incremented
        _sut.HasEverBeenIncremented.Should().BeFalse();
    }

    [Fact]
    public void HasEverBeenIncremented_AfterIncrementByPositive_IsTrue()
    {
        // Validates: Req 3.9 — once events arrive, stale indicator no longer shown
        _sut.IncrementBy(1);

        _sut.HasEverBeenIncremented.Should().BeTrue();
    }

    [Fact]
    public void HasEverBeenIncremented_AfterIncrementByZero_RemainseFalse()
    {
        // IncrementBy(0) is a no-op — should not count as "ever incremented"
        _sut.IncrementBy(0);

        _sut.HasEverBeenIncremented.Should().BeFalse();
    }

    [Fact]
    public void HasEverBeenIncremented_AfterReset_RemainsTrue()
    {
        // Validates: Req 3.9 — Reset resets the count but NOT the ever-incremented flag;
        // "visited page" and "stale instance" must remain distinguishable.
        _sut.IncrementBy(3);
        _sut.Reset();

        _sut.HasEverBeenIncremented.Should().BeTrue();
    }
}


// Additional thread-safety tests for uncovered concurrent-access paths

public sealed class ConsolidationBadgeServiceConcurrencyTests
{
    [Fact]
    public void IncrementBy_ConcurrentCalls_ProducesCorrectFinalCount()
    {
        // Validates: thread-safety under concurrent IncrementBy calls (lock path)
        var sut = new ConsolidationBadgeService();
        const int threadCount = 10;
        const int incrementsPerThread = 100;

        Parallel.For(0, threadCount, _ =>
        {
            for (int i = 0; i < incrementsPerThread; i++)
                sut.IncrementBy(1);
        });

        sut.BadgeCount.Should().Be(threadCount * incrementsPerThread);
    }

    [Fact]
    public void Reset_ConcurrentWithIncrementBy_NeverProducesNegativeCount()
    {
        // Validates: Reset and IncrementBy racing together never produce a negative count
        var sut = new ConsolidationBadgeService();
        const int ops = 200;

        Parallel.For(0, ops, i =>
        {
            if (i % 2 == 0)
                sut.IncrementBy(1);
            else
                sut.Reset();
        });

        sut.BadgeCount.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void HasEverBeenIncremented_ConcurrentIncrements_NeverFlipsBackToFalse()
    {
        // Once HasEverBeenIncremented is true, it must never revert under concurrency
        var sut = new ConsolidationBadgeService();

        // Set it to true first
        sut.IncrementBy(1);

        Parallel.For(0, 100, _ =>
        {
            sut.Reset();
            // This should never set HasEverBeenIncremented back to false
        });

        sut.HasEverBeenIncremented.Should().BeTrue(
            "Reset must never clear HasEverBeenIncremented");
    }
}
