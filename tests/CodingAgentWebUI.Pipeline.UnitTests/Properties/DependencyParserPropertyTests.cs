using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for DependencyParser.
/// Verifies crash-freedom: Parse never throws for any string input.
/// Also verifies structural invariants (output always non-negative, always deduplicated).
/// </summary>
[Trait("Feature", "027-issue-dependency-tracking")]
public class DependencyParserPropertyTests
{
    /// <summary>
    /// Crash-freedom: Parse never throws an exception for any arbitrary string input.
    /// This covers malicious regex inputs, unicode, control characters, null bytes, etc.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Parse_NeverThrows_ForAnyInput(string? body)
    {
        // Act — should not throw
        var result = DependencyParser.Parse(body);

        // Assert — result is always a valid list
        return result is not null;
    }

    /// <summary>
    /// Crash-freedom with selfIdentifier: Parse never throws regardless of selfIdentifier value.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Parse_WithSelfIdentifier_NeverThrows(string? body, int? selfIdentifier)
    {
        var result = DependencyParser.Parse(body, selfIdentifier);

        return result is not null;
    }

    /// <summary>
    /// All returned issue numbers are strictly positive.
    /// The implementation filters out zero and negative numbers.
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Parse_AllResults_ArePositive(string? body)
    {
        var result = DependencyParser.Parse(body);

        return result.All(n => n > 0);
    }

    /// <summary>
    /// Results are always unique (no duplicates in output).
    /// </summary>
    [Property(MaxTest = 200)]
    public bool Parse_Results_AreAlwaysUnique(string? body)
    {
        var result = DependencyParser.Parse(body);

        return result.Count == result.Distinct().Count();
    }

    /// <summary>
    /// When selfIdentifier is provided, it never appears in results.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Parse_SelfIdentifier_NeverInResults(NonEmptyString bodyNes, PositiveInt selfId)
    {
        var body = $"Blocked by #{selfId.Get} and depends on #{selfId.Get + 1}";
        var result = DependencyParser.Parse(body, selfId.Get);

        return !result.Contains(selfId.Get);
    }

    /// <summary>
    /// Embedding a known dependency pattern always produces at least one result
    /// (unless the number is zero or equals selfIdentifier).
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Parse_WithEmbeddedPattern_FindsDependency(PositiveInt issueNumber)
    {
        var body = $"Some context text. Blocked by #{issueNumber.Get}. More text.";
        var result = DependencyParser.Parse(body);

        return result.Contains(issueNumber.Get);
    }

    /// <summary>
    /// Idempotence: parsing the same body twice yields the same results.
    /// </summary>
    [Property(MaxTest = 100)]
    public bool Parse_IsIdempotent(string? body)
    {
        var result1 = DependencyParser.Parse(body);
        var result2 = DependencyParser.Parse(body);

        return result1.SequenceEqual(result2);
    }
}
