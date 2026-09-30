using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

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

    [Theory]
    [InlineData("Blocked by #123", 123)]
    [InlineData("Depends on #456", 456)]
    [InlineData("Requires #789", 789)]
    [InlineData("After #42", 42)]
    public void Parse_RecognizedPattern_ReturnsNumberRef(string body, int expectedIssue)
    {
        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(expectedIssue);
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

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(10);
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

        result.Should().HaveCount(4);
        result.OfType<NumberRef>().Select(r => r.Number).Should().BeEquivalentTo(new[] { 10, 20, 30, 40 });
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

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(10);
    }

    // ─── 6. Self-reference filtering ────────────────────────────────────────────

    [Fact]
    public void Parse_SelfReference_ExcludesSelf()
    {
        var body = "Blocked by #5 and depends on #10";

        var result = DependencyParser.Parse(body, selfIdentifier: 5);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(10);
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

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(42);
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

    [Theory]
    [InlineData("I fixed this hereafter #123 was reported")]
    [InlineData("The prerequires #456 step is done")]
    [InlineData("thereafter #789 was completed")]
    public void Parse_WordContainingKeyword_DoesNotMatch(string body)
    {
        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    // ─── 9. Patterns embedded in larger text ────────────────────────────────────

    [Theory]
    [InlineData("This task is blocked by #15 and should wait.", 15)]
    [InlineData("Blocked by #7\nSome other text", 7)]
    public void Parse_PatternEmbeddedInText_Matches(string body, int expectedIssue)
    {
        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(expectedIssue);
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

    [Theory]
    [InlineData("Blocked  by  #100", 100)]
    [InlineData("Depends\ton\t#200", 200)]
    public void Parse_FlexibleWhitespace_Matches(string body, int expectedIssue)
    {
        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<NumberRef>()
            .Which.Number.Should().Be(expectedIssue);
    }

    // ─── 12. GitHub issue URL references (AC3) ──────────────────────────────────

    [Fact]
    public void Parse_GitHubIssueUrl_ReturnsUrlRef()
    {
        var body = "Blocked by https://github.com/acme/repo/issues/40";

        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<UrlRef>()
            .Which.Url.Should().Be("https://github.com/acme/repo/issues/40");
    }

    [Fact]
    public void Parse_GitHubIssueUrl_DependsOnKeyword_ReturnsUrlRef()
    {
        var body = "Depends on https://github.com/org/project/issues/123";

        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<UrlRef>()
            .Which.Url.Should().Be("https://github.com/org/project/issues/123");
    }

    // ─── 13. GitLab issue URL references (AC3) ──────────────────────────────────

    [Fact]
    public void Parse_GitLabIssueUrl_ReturnsUrlRef()
    {
        // GitLab uses /-/issues/ path separator
        var body = "Depends on https://gitlab.com/org/proj/-/issues/7";

        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<UrlRef>()
            .Which.Url.Should().Be("https://gitlab.com/org/proj/-/issues/7");
    }

    [Fact]
    public void Parse_GitLabIssueUrl_RequiresKeyword_ReturnsUrlRef()
    {
        var body = "Requires https://gitlab.com/mygroup/myproject/-/issues/99";

        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<UrlRef>()
            .Which.Url.Should().Be("https://gitlab.com/mygroup/myproject/-/issues/99");
    }

    // ─── 14. Mixed number and URL references ────────────────────────────────────

    [Fact]
    public void Parse_MixedNumberAndUrl_ReturnsBoth()
    {
        var body = "Blocked by #10\nDepends on https://github.com/acme/api/issues/40";

        var result = DependencyParser.Parse(body);

        result.Should().HaveCount(2);
        result.OfType<NumberRef>().Should().ContainSingle().Which.Number.Should().Be(10);
        result.OfType<UrlRef>().Should().ContainSingle()
            .Which.Url.Should().Be("https://github.com/acme/api/issues/40");
    }

    // ─── 15. Non-issue URLs are not matched ─────────────────────────────────────

    [Fact]
    public void Parse_GitHubPullRequestUrl_Ignored()
    {
        // PR URLs (/pull/) must not be matched
        var body = "Blocked by https://github.com/acme/repo/pull/40";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GitLabMergeRequestUrl_Ignored()
    {
        var body = "Depends on https://gitlab.com/org/proj/-/merge_requests/7";

        var result = DependencyParser.Parse(body);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Parse_GitLabIssueUrlWithoutDashSlash_NotMatched()
    {
        // GitLab issues require /-/issues/, not /issues/
        var body = "Depends on https://gitlab.com/org/proj/issues/7";

        var result = DependencyParser.Parse(body);

        // Not matched as a GitLab URL; also not a GitHub URL — result must be empty
        result.Should().BeEmpty();
    }

    // ─── 16. Duplicate URL deduplication ────────────────────────────────────────

    [Fact]
    public void Parse_DuplicateUrls_ReturnsUnique()
    {
        var body = """
            Blocked by https://github.com/acme/repo/issues/40
            Depends on https://github.com/acme/repo/issues/40
            """;

        var result = DependencyParser.Parse(body);

        result.Should().ContainSingle()
            .Which.Should().BeOfType<UrlRef>()
            .Which.Url.Should().Be("https://github.com/acme/repo/issues/40");
    }
}
