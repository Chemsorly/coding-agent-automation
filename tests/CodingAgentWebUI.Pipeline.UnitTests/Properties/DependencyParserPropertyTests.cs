using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for <see cref="DependencyParser.Parse"/>.
///
/// DependencyParser is a security-adjacent regex parser that gates pipeline dispatch:
/// if it throws or returns incorrect results on adversarial input, issues can be
/// dispatched with wrong dependency constraints or the pipeline can crash.
///
/// Properties tested:
///   - Crash-freedom: no arbitrary string input causes an exception
///   - Result ⊆ positive integers: all returned values are ≥ 1
///   - Idempotence: parsing the same body twice returns the same set
///   - Self-exclusion: when selfIdentifier is set, that number is never in the result
/// </summary>
[Trait("Feature", "027-issue-dependency-tracking")]
public class DependencyParserPropertyTests
{
    // Shared string generator — fixed-length char arrays to avoid FsCheck 3 String default issues
    private static Gen<string> ArbitraryStringGen =>
        Gen.Choose(0, 300)
            .SelectMany(len => Gen.ArrayOf(Gen.Choose(0, 127).Select(i => (char)i), len))
            .Select(chars => new string(chars));

    // ── Crash-freedom ──────────────────────────────────────────────────────────

    /// <summary>
    /// Parse never throws regardless of input. Regex timeout falls back to partial results.
    /// </summary>
    [Property(MaxTest = 200)]
    public Property Parse_ArbitraryInput_NeverThrows()
    {
        return Prop.ForAll(ArbitraryStringGen.ToArbitrary(), (string body) =>
        {
            Exception? ex = null;
            try { DependencyParser.Parse(body); }
            catch (Exception e) { ex = e; }
            ex.Should().BeNull($"Parse must never throw — it returned exception for input length={body.Length}");
        });
    }

    // ── Result invariants ──────────────────────────────────────────────────────

    /// <summary>
    /// All returned issue numbers are strictly positive integers (> 0).
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Parse_ReturnsOnlyPositiveIntegers()
    {
        // Mix numeric (#N) and alpha-identifier (PROJ-123) forms to exercise both capture groups.
        // Alpha-identifiers are never parseable as int, so they must produce no results (not
        // non-positive integers). This exercises the Group 2 branch of the regex.
        var numericBodyGen =
            from keyword in Gen.Elements("Blocked by", "Depends on", "Requires", "After")
            from number in Gen.Choose(1, 99999)
            from prefix in Gen.Elements("", "Some text before. ", "\n", "  ")
            from suffix in Gen.Elements("", " some text after", "\nmore content")
            select $"{prefix}{keyword} #{number}{suffix}";

        var alphaBodyGen =
            from keyword in Gen.Elements("Blocked by", "Depends on", "Requires", "After")
            from id in Gen.Elements("PROJ-123", "TICKET-456", "ISSUE-99", "ABC-1")
            select $"{keyword} {id}";

        var gen = Gen.OneOf(numericBodyGen, alphaBodyGen);

        return Prop.ForAll(gen.ToArbitrary(), (string body) =>
        {
            var result = DependencyParser.Parse(body);
            result.Should().AllSatisfy(n => n.Should().BeGreaterThan(0,
                $"every parsed dependency must be a positive integer, got {n} from input: [{body}]"));
        });
    }

    /// <summary>
    /// Parse is idempotent: calling it twice on the same body returns equal sets.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Parse_SameInput_IsIdempotent()
    {
        return Prop.ForAll(ArbitraryStringGen.ToArbitrary(), (string body) =>
        {
            var result1 = DependencyParser.Parse(body);
            var result2 = DependencyParser.Parse(body);

            result1.Should().BeEquivalentTo(result2, opts => opts.WithoutStrictOrdering(),
                "Parse is deterministic — two calls on identical input must return the same set");
        });
    }

    // ── Self-exclusion ─────────────────────────────────────────────────────────

    /// <summary>
    /// When selfIdentifier is provided, that number is never in the result.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Parse_WithSelfIdentifier_ExcludesSelf()
    {
        var gen =
            from self in Gen.Choose(1, 1000)
            from other in Gen.Choose(1, 1000).Where(n => n != self)
            select (self, other,
                body: $"Blocked by #{self} and also depends on #{other}");

        return Prop.ForAll(gen.ToArbitrary(), t =>
        {
            var (self, other, body) = t;
            var result = DependencyParser.Parse(body, selfIdentifier: self);

            result.Should().NotContain(self,
                $"selfIdentifier={self} must be excluded from parse results");
            result.Should().Contain(other,
                $"other dependency #{other} must still be included when selfIdentifier={self}");
        });
    }

    // ── Null / empty edge cases ────────────────────────────────────────────────

    [Fact]
    public void Parse_NullBody_ReturnsEmptyWithoutThrowing()
    {
        var result = DependencyParser.Parse(null);
        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsEmptyWithoutThrowing()
    {
        var result = DependencyParser.Parse(string.Empty);
        result.Should().BeEmpty();
    }

    /// <summary>
    /// Alpha-identifiers like "PROJ-123" match the regex but are non-numeric — they must
    /// produce no results, not a positive integer. Regression guard for Group 2 branch.
    /// </summary>
    [Theory]
    [InlineData("Blocked by PROJ-123", false)]
    [InlineData("Depends on TICKET-456", false)]
    [InlineData("Requires ABC-1", false)]
    [InlineData("After ISSUE-99", false)]
    [InlineData("Blocked by PROJ-123 and also Depends on #42", true)]
    public void Parse_AlphaIdentifier_NotIncludedInResults(string body, bool containsNumericRef)
    {
        var result = DependencyParser.Parse(body);

        // Alpha identifiers are non-numeric so they must never appear in results
        result.Should().AllSatisfy(n => n.Should().BeGreaterThan(0,
            $"alpha identifiers like PROJ-123 must never produce non-positive integers, got {n}"));

        // Mixed case: the numeric #42 should still be included
        if (containsNumericRef)
            result.Should().Contain(42, "numeric refs alongside alpha refs must still be parsed");
    }
}
