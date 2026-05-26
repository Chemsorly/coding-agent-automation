using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for DependencyParser — validates extraction of issue dependency references
/// from issue body text using word-boundary regex matching.
/// </summary>
[Trait("Feature", "027-issue-dependency-tracking")]
public class DependencyParserTests
{
    // ─── 1. Null/empty input ────────────────────────────────────────────────────

    [Fact]
    public void Parse_NullBody_ReturnsEmptyList()
    {
        var result = DependencyParser.Parse(null);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_EmptyBody_ReturnsEmptyList()
    {
        var result = DependencyParser.Parse(string.Empty);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_WhitespaceBody_ReturnsEmptyList()
    {
        var result = DependencyParser.Parse("   \n\t  ");

        result.Should().BeEmpty();
    }

    // ─── 2. Pattern recognition ─────────────────────────────────────────────────

    [Fact]
    public void Parse_BlockedByPattern_ReturnsIssueNumber()
    {
        var result = DependencyParser.Parse("Blocked by #123");

        result.Should().BeEquivalentTo(new[] { 123 });
    }

    [Fact]
    public void Parse_DependsOnPattern_ReturnsIssueNumber()
    {
        var result = DependencyParser.Parse("Depends on #456");

        result.Should().BeEquivalentTo(new[] { 456 });
    }

    [Fact]
    public void Parse_RequiresPattern_ReturnsIssueNumber()
    {
        var result = DependencyParser.Parse("Requires #789");

        result.Should().BeEquivalentTo(new[] { 789 });
    }

    [Fact]
    public void Parse_AfterPattern_ReturnsIssueNumber()
    {
        var result = DependencyParser.Parse("After #42");

        result.Should().BeEquivalentTo(new[] { 42 });
    }

    // ─── 3. Case-insensitive matching ───────────────────────────────────────────

    [Theory]
    [InlineData("BLOCKED BY #10")]
    [InlineData("blocked by #10")]
    [InlineData("Blocked By #10")]
    [InlineData("DEPENDS ON #10")]
    [InlineData("depends on #10")]
    [InlineData("Depends On #10")]
    [InlineData("REQUIRES #10")]
    [InlineData("requires #10")]
    [InlineData("AFTER #10")]
    [InlineData("after #10")]
    public void Parse_CaseInsensitive_MatchesAllVariants(string body)
    {
        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 10 });
    }

    // ─── 4. Multiple dependencies ───────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleDependencies_ReturnsAll()
    {
        var body = """
            This issue is blocked by #10 and also depends on #20.
            It requires #30 to be done first.
            After #40 is merged, we can start.
            """;

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 10, 20, 30, 40 });
    }

    // ─── 5. Deduplication ───────────────────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateReferences_ReturnsUnique()
    {
        var body = """
            Blocked by #10
            Depends on #10
            Requires #10
            """;

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 10 });
    }

    // ─── 6. Self-reference filtering ────────────────────────────────────────────

    [Fact]
    public void Parse_SelfReference_ExcludesSelf()
    {
        var body = "Blocked by #5 and depends on #10";

        var result = DependencyParser.Parse(body, selfIdentifier: 5);

        result.Should().BeEquivalentTo(new[] { 10 });
    }

    [Fact]
    public void Parse_OnlySelfReference_ReturnsEmpty()
    {
        var body = "Depends on #42";

        var result = DependencyParser.Parse(body, selfIdentifier: 42);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NullSelfIdentifier_DoesNotFilter()
    {
        var body = "Depends on #42";

        var result = DependencyParser.Parse(body, selfIdentifier: null);

        result.Should().BeEquivalentTo(new[] { 42 });
    }

    // ─── 7. Non-positive integers ───────────────────────────────────────────────

    [Fact]
    public void Parse_ZeroIssueNumber_Ignored()
    {
        var body = "Depends on #0";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    // ─── 8. Word boundary — false positive prevention ───────────────────────────

    [Fact]
    public void Parse_HereafterPattern_DoesNotMatch()
    {
        var body = "I fixed this hereafter #123 was reported";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_PrerequiresPattern_DoesNotMatch()
    {
        var body = "The prerequires #456 step is done";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ThereafterPattern_DoesNotMatch()
    {
        var body = "thereafter #789 was completed";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    // ─── 9. Patterns embedded in larger text ────────────────────────────────────

    [Fact]
    public void Parse_PatternInMiddleOfSentence_Matches()
    {
        var body = "This task is blocked by #15 and should wait.";

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 15 });
    }

    [Fact]
    public void Parse_PatternAtStartOfLine_Matches()
    {
        var body = "Blocked by #7\nSome other text";

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 7 });
    }

    // ─── 10. Malformed references ───────────────────────────────────────────────

    [Fact]
    public void Parse_NonNumericReference_Ignored()
    {
        var body = "Blocked by #abc";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_NoHashSymbol_Ignored()
    {
        var body = "Blocked by 123";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    // ─── 11. Whitespace flexibility ─────────────────────────────────────────────

    [Fact]
    public void Parse_MultipleSpacesBetweenKeywords_Matches()
    {
        var body = "Blocked  by  #100";

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 100 });
    }

    [Fact]
    public void Parse_TabBetweenKeywords_Matches()
    {
        var body = "Depends\ton\t#200";

        var result = DependencyParser.Parse(body);

        result.Should().BeEquivalentTo(new[] { 200 });
    }
}
