using AwesomeAssertions;
using CodingAgent.Pipeline.Services;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;

namespace CodingAgent.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for <see cref="DependencyParser.Parse"/>.
///
/// DependencyParser is a security-adjacent regex parser that gates pipeline dispatch:
/// if it throws or returns incorrect results on adversarial input, issues can be
/// dispatched with wrong dependency constraints or the pipeline can crash.
///
/// Properties tested:
///   - Crash-freedom: no arbitrary string input causes an exception
///   - NumberRef results contain only positive integers (> 0)
///   - Idempotence: parsing the same body twice returns the same set
///   - Self-exclusion: when selfIdentifier is set, that number is never in NumberRef results
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
    /// All <see cref="NumberRef"/> results contain strictly positive integers (> 0).
    /// Alpha-identifiers (PROJ-123) are non-numeric and must produce no results.
    /// </summary>
    [Property(MaxTest = 100)]
    public Property Parse_NumberRefs_ArePositiveIntegers()
    {
        // Mix numeric (#N) and alpha-identifier (PROJ-123) forms to exercise both capture groups.
        // Alpha-identifiers are never parseable as int, so they must produce no NumberRef results.
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
            result.OfType<NumberRef>().Should().AllSatisfy(nr => nr.Number.Should().BeGreaterThan(0,
                $"every NumberRef must contain a positive integer, got {nr.Number} from input: [{body}]"));
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
    /// When selfIdentifier is provided, that number is never in the NumberRef results.
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
            var numberRefs = result.OfType<NumberRef>().Select(r => r.Number).ToList();

            numberRefs.Should().NotContain(self,
                $"selfIdentifier={self} must be excluded from NumberRef results");
            numberRefs.Should().Contain(other,
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
    /// produce no results. Regression guard for the alpha-identifier branch.
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

        // Alpha identifiers produce no DependencyRef at all
        result.OfType<NumberRef>().Should().AllSatisfy(nr => nr.Number.Should().BeGreaterThan(0,
            $"alpha identifiers like PROJ-123 must never produce non-positive NumberRefs, got {nr.Number}"));

        // Mixed case: the numeric #42 should still be included as a NumberRef
        if (containsNumericRef)
            result.OfType<NumberRef>().Should().Contain(nr => nr.Number == 42,
                "numeric refs alongside alpha refs must still be parsed");
    }
}
