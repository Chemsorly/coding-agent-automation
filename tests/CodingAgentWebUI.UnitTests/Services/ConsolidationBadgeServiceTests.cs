using AwesomeAssertions;
using CodingAgentWebUI.Services;

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
}
