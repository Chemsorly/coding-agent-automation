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
        var bodyGen =
            from keyword in Gen.Elements("Blocked by", "Depends on", "Requires", "After")
            from number in Gen.Choose(1, 99999)
            from prefix in Gen.Elements("", "Some text before. ", "\n", "  ")
            from suffix in Gen.Elements("", " some text after", "\nmore content")
            select $"{prefix}{keyword} #{number}{suffix}";

        return Prop.ForAll(bodyGen.ToArbitrary(), (string body) =>
        {
            var result = DependencyParser.Parse(body);
            result.Should().AllSatisfy(n => n.Should().BeGreaterThan(0,
                $"every parsed dependency must be a positive integer, got {n}"));
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
}
