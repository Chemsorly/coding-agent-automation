// Feature: 021-consolidation-loops
// Property 10: Badge Count Equals Sum of New Issues and Suggestions
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Services;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Property-based tests for ConsolidationBadgeService.
/// **Validates: Requirements 10.1, 10.3**
/// </summary>
public class ConsolidationBadgeServicePropertyTests
{
    /// <summary>
    /// Property 10: Badge Count Equals Sum of New Issues and Suggestions
    /// For any sequence of IncrementBy calls with non-negative integers,
    /// BadgeCount equals their sum; after Reset(), BadgeCount is zero.
    /// **Validates: Requirements 10.1, 10.3**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(BadgeArbitraries) })]
    public void BadgeCount_EqualsSum_OfAllIncrements(List<int> increments)
    {
        var sut = new ConsolidationBadgeService();

        var expectedSum = 0;
        foreach (var increment in increments)
        {
            sut.IncrementBy(increment);
            expectedSum += increment;
        }

        sut.BadgeCount.Should().Be(expectedSum,
            $"badge count should equal sum of all increments ({string.Join("+", increments)} = {expectedSum})");
    }

    /// <summary>
    /// Property 10 (reset): After any sequence of increments followed by Reset(),
    /// BadgeCount is always zero.
    /// **Validates: Requirements 10.3**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(BadgeArbitraries) })]
    public void BadgeCount_AfterReset_IsAlwaysZero(List<int> increments)
    {
        var sut = new ConsolidationBadgeService();

        foreach (var increment in increments)
            sut.IncrementBy(increment);

        sut.Reset();

        sut.BadgeCount.Should().Be(0, "badge count should be zero after Reset()");
    }

    /// <summary>
    /// Property 10 (interleaved): For any interleaved sequence of increments and resets,
    /// the badge count equals the sum of increments since the last reset.
    /// **Validates: Requirements 10.1, 10.3**
    /// </summary>
    [Property(Arbitrary = new[] { typeof(BadgeArbitraries) })]
    public void BadgeCount_AfterInterleavedOps_EqualsSumSinceLastReset(
        List<BadgeOperation> operations)
    {
        var sut = new ConsolidationBadgeService();

        var sumSinceReset = 0;
        foreach (var op in operations)
        {
            if (op.IsReset)
            {
                sut.Reset();
                sumSinceReset = 0;
            }
            else
            {
                sut.IncrementBy(op.Value);
                sumSinceReset += op.Value;
            }
        }

        sut.BadgeCount.Should().Be(sumSinceReset);
    }
}

/// <summary>
/// Represents either an IncrementBy(value) or a Reset() operation.
/// </summary>
public sealed class BadgeOperation
{
    public bool IsReset { get; init; }
    public int Value { get; init; }

    public override string ToString() => IsReset ? "Reset()" : $"IncrementBy({Value})";
}

/// <summary>
/// FsCheck arbitrary generators for badge service property tests.
/// </summary>
public class BadgeArbitraries
{
    public static Arbitrary<List<int>> IncrementListArb()
    {
        return Gen.Choose(0, 10)
            .SelectMany(count => Gen.ArrayOf(Gen.Choose(0, 100), count))
            .Select(arr => arr.ToList())
            .ToArbitrary();
    }

    public static Arbitrary<List<BadgeOperation>> OperationListArb()
    {
        var opGen = Gen.Frequency(
            (3, Gen.Choose(0, 50).Select(v => new BadgeOperation { IsReset = false, Value = v })),
            (1, Gen.Constant(new BadgeOperation { IsReset = true, Value = 0 })));

        return Gen.Choose(0, 10)
            .SelectMany(count => Gen.ArrayOf(opGen, count))
            .Select(arr => arr.ToList())
            .ToArbitrary();
    }
}
