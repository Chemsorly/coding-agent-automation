using AwesomeAssertions;
using CodingAgentWebUI.Hub;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ConsolidationBadgeService — pure thread-safe state machine.
/// </summary>
public sealed class ConsolidationBadgeServiceTests
{
    private readonly ConsolidationBadgeService _sut = new();

    // ── Initial state ─────────────────────────────────────────────────────

    [Fact]
    public void InitialBadgeCount_IsZero()
        => _sut.BadgeCount.Should().Be(0);

    [Fact]
    public void HasEverBeenIncremented_InitiallyFalse()
        => _sut.HasEverBeenIncremented.Should().BeFalse();

    // ── IncrementBy ───────────────────────────────────────────────────────

    [Fact]
    public void IncrementBy_PositiveValue_AddsToBadgeCount()
    {
        _sut.IncrementBy(3);
        _sut.BadgeCount.Should().Be(3);
    }

    [Fact]
    public void IncrementBy_MultipleIncrements_Accumulates()
    {
        _sut.IncrementBy(2);
        _sut.IncrementBy(5);
        _sut.BadgeCount.Should().Be(7);
    }

    [Fact]
    public void IncrementBy_SetsHasEverBeenIncremented()
    {
        _sut.IncrementBy(1);
        _sut.HasEverBeenIncremented.Should().BeTrue();
    }

    [Fact]
    public void IncrementBy_Zero_DoesNotChangeBadgeCount()
    {
        _sut.IncrementBy(0);
        _sut.BadgeCount.Should().Be(0);
    }

    [Fact]
    public void IncrementBy_Zero_DoesNotSetHasEverBeenIncremented()
    {
        _sut.IncrementBy(0);
        _sut.HasEverBeenIncremented.Should().BeFalse();
    }

    [Fact]
    public void IncrementBy_NegativeValue_Throws()
    {
        var act = () => _sut.IncrementBy(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void IncrementBy_FiresOnBadgeChangedEvent()
    {
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;
        _sut.IncrementBy(1);
        fired.Should().BeTrue();
    }

    [Fact]
    public void IncrementBy_Zero_DoesNotFireEvent()
    {
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;
        _sut.IncrementBy(0);
        fired.Should().BeFalse();
    }

    // ── Reset ─────────────────────────────────────────────────────────────

    [Fact]
    public void Reset_SetsBadgeCountToZero()
    {
        _sut.IncrementBy(5);
        _sut.Reset();
        _sut.BadgeCount.Should().Be(0);
    }

    [Fact]
    public void Reset_PreservesHasEverBeenIncremented()
    {
        _sut.IncrementBy(1);
        _sut.Reset();
        _sut.HasEverBeenIncremented.Should().BeTrue();
    }

    [Fact]
    public void Reset_WhenAlreadyZero_DoesNotFireEvent()
    {
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;
        _sut.Reset(); // already 0
        fired.Should().BeFalse();
    }

    [Fact]
    public void Reset_WhenNonZero_FiresOnBadgeChangedEvent()
    {
        _sut.IncrementBy(3);
        var fired = false;
        _sut.OnBadgeChanged += () => fired = true;
        _sut.Reset();
        fired.Should().BeTrue();
    }

    // ── Increment then reset cycle ────────────────────────────────────────

    [Fact]
    public void IncrementThenReset_ThenIncrementAgain_Accumulates()
    {
        _sut.IncrementBy(5);
        _sut.Reset();
        _sut.IncrementBy(3);
        _sut.BadgeCount.Should().Be(3);
    }
}
