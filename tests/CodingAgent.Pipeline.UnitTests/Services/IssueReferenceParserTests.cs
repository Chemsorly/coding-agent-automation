using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for IssueReferenceParser — validates extraction of issue references
/// from PR/MR title and description text.
/// </summary>
public class IssueReferenceParserTests
{
    private static readonly string[] ExpectedMultiVerbFormIssues = ["1", "2", "3", "4"];
    // ─── ParseClosingKeywords (GitLab-compatible) ───────────────────────────────

    [Fact]
    public void ParseClosingKeywords_NullText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseClosingKeywords(null, results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseClosingKeywords_EmptyText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseClosingKeywords("", results);
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Closes #42", "42")]
    [InlineData("Fixes #7", "7")]
    [InlineData("Resolves #100", "100")]
    [InlineData("closes #1", "1")]
    [InlineData("FIXES #99", "99")]
    public void ParseClosingKeywords_BaseKeywords_ExtractsNumber(string text, string expected)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseClosingKeywords(text, results);
        results.Should().Contain(expected);
    }

    [Fact]
    public void ParseClosingKeywords_MultipleMatches_ExtractsAll()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseClosingKeywords("Closes #1\nFixes #2\nResolves #3", results);
        results.Should().BeEquivalentTo(new[] { "1", "2", "3" });
    }

    [Theory]
    [InlineData("closed #5")]
    [InlineData("fixed #5")]
    [InlineData("resolved #5")]
    [InlineData("GH-5")]
    public void ParseClosingKeywords_NonBaseKeywords_DoesNotMatch(string text)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseClosingKeywords(text, results);
        results.Should().BeEmpty();
    }

    // ─── ParseIssueReferences (GitHub-compatible) ───────────────────────────────

    [Fact]
    public void ParseIssueReferences_NullText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences(null, results);
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("closes #10", "10")]
    [InlineData("closed #10", "10")]
    [InlineData("fix #10", "10")]
    [InlineData("fixes #10", "10")]
    [InlineData("fixed #10", "10")]
    [InlineData("resolve #10", "10")]
    [InlineData("resolves #10", "10")]
    [InlineData("resolved #10", "10")]
    public void ParseIssueReferences_AllVerbForms_ExtractsNumber(string text, string expected)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences(text, results);
        results.Should().Contain(expected);
    }

    [Fact]
    public void ParseIssueReferences_GhDashInClosingKeyword_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences("closes GH-55", results);
        results.Should().Contain("55");
    }

    [Fact]
    public void ParseIssueReferences_CrossRepo_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences("Related to myorg/myrepo#123", results);
        results.Should().Contain("123");
    }

    [Fact]
    public void ParseIssueReferences_GhDash_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences("Implements GH-42 feature", results);
        results.Should().Contain("42");
    }

    [Fact]
    public void ParseIssueReferences_SimpleHash_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences("See #77 for details", results);
        results.Should().Contain("77");
    }

    [Fact]
    public void ParseIssueReferences_CombinedPatterns_Deduplicates()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueReferences("Closes #5 and also #5", results);
        results.Should().ContainSingle().Which.Should().Be("5");
    }

    // ─── ParseAllClosingKeywords (GitHub + GitLab closing keyword forms) ─────────

    [Fact]
    public void ParseAllClosingKeywords_NullText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords(null, results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseAllClosingKeywords_EmptyText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords("", results);
        results.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Closes #42", "42")]
    [InlineData("Fixes #7", "7")]
    [InlineData("Resolves #100", "100")]
    [InlineData("closes #1", "1")]
    [InlineData("FIXES #99", "99")]
    public void ParseAllClosingKeywords_GitLabBaseForms_ExtractsNumber(string text, string expected)
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords(text, results);
        results.Should().Contain(expected);
    }

    [Theory]
    [InlineData("closed #5", "5")]
    [InlineData("fixed #5", "5")]
    [InlineData("resolved #5", "5")]
    [InlineData("close #5", "5")]
    [InlineData("fix #5", "5")]
    [InlineData("resolve #5", "5")]
    [InlineData("Fixed #99", "99")]
    [InlineData("Closed #99", "99")]
    [InlineData("Resolved #99", "99")]
    public void ParseAllClosingKeywords_GitHubAllVerbForms_ExtractsNumber(string text, string expected)
    {
        // These past-tense and non-base verb forms are valid GitHub closing keywords but are
        // NOT matched by ParseClosingKeywords (GitLab pattern only). ParseAllClosingKeywords
        // must handle them — this is the CRITICAL fix from the .NET Specialist review.
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords(text, results);
        results.Should().Contain(expected,
            $"'{text}' is a valid GitHub closing keyword and must populate LinkedIssueContexts");
    }

    [Theory]
    [InlineData("GH-5")]        // standalone GH-N without a keyword — ParseAllClosingKeywords must NOT match this
    [InlineData("myorg/repo#5")] // cross-repo reference — must not match
    [InlineData("See #5 for details")] // plain #N mention — must not match
    public void ParseAllClosingKeywords_NonKeywordPatterns_DoesNotMatch(string text)
    {
        // ParseAllClosingKeywords deliberately excludes standalone GH-N, cross-repo, and plain #N
        // to avoid over-matching PR description prose. Only closing-keyword forms are matched.
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords(text, results);
        results.Should().BeEmpty(
            $"'{text}' is not a closing keyword reference and must not be matched by ParseAllClosingKeywords");
    }

    [Fact]
    public void ParseAllClosingKeywords_MultipleVerbForms_ExtractsAll()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords("Fixes #1\nFixed #2\nClosed #3\nResolves #4", results);
        results.Should().BeEquivalentTo(ExpectedMultiVerbFormIssues);
    }

    [Fact]
    public void ParseAllClosingKeywords_DuplicateReferences_Deduplicates()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseAllClosingKeywords("Fixes #99\nFixed #99", results);
        results.Should().ContainSingle().Which.Should().Be("99");
    }

    // ─── ParseIssueUrls (GitHub issue URL pattern) ──────────────────────────────

    [Fact]
    public void ParseIssueUrls_NullText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls(null, results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseIssueUrls_EmptyText_DoesNothing()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("", results);
        results.Should().BeEmpty();
        // TODO: [WARNING] Missing test for whitespace-only input (e.g. "   "). IsNullOrWhiteSpace covers
        // it as a distinct branch from null and "". Add ParseIssueUrls_WhitespaceOnlyText_DoesNothing
        // consistent with the guard in other ParseAll* methods in this class.
    }

    [Fact]
    public void ParseIssueUrls_StandardGitHubIssueUrl_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("https://github.com/owner/repo/issues/42", results);
        results.Should().ContainSingle().Which.Should().Be("42");
    }

    [Fact]
    public void ParseIssueUrls_IssueUrlEmbeddedInText_ExtractsNumber()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls(
            "See https://github.com/Chemsorly/coding-agent-automation/issues/99 for context.",
            results);
        results.Should().ContainSingle().Which.Should().Be("99");
    }

    [Fact]
    public void ParseIssueUrls_PullRequestUrl_DoesNotMatch()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("https://github.com/owner/repo/pull/42", results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseIssueUrls_CommitUrl_DoesNotMatch()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("https://github.com/owner/repo/commit/abc1234567890", results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseIssueUrls_RawContentUrl_DoesNotMatch()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("https://raw.githubusercontent.com/owner/repo/main/file.md", results);
        results.Should().BeEmpty();
    }

    [Fact]
    public void ParseIssueUrls_MultipleUrlsInText_ExtractsAll()
    {
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls(
            "Relates to https://github.com/owner/repo/issues/10 and https://github.com/owner/repo/issues/20.",
            results);
        results.Should().BeEquivalentTo(new[] { "10", "20" });
    }

    [Fact]
    public void ParseIssueUrls_DeduplicationWithClosingKeyword_SameIssueNumber()
    {
        // When both ParseAllClosingKeywords and ParseIssueUrls target the same HashSet,
        // a number present in both sources is stored only once (HashSet dedup).
        var results = new HashSet<string>(StringComparer.Ordinal);
        const string text = "Closes #99\nSee https://github.com/owner/repo/issues/99 for details.";
        IssueReferenceParser.ParseAllClosingKeywords(text, results);
        IssueReferenceParser.ParseIssueUrls(text, results);
        results.Should().ContainSingle().Which.Should().Be("99");
    }

    [Fact]
    public void ParseIssueUrls_HttpUrl_Matches()
    {
        // The regex uses https?:// so plain http:// must also be matched.
        var results = new HashSet<string>(StringComparer.Ordinal);
        IssueReferenceParser.ParseIssueUrls("http://github.com/owner/repo/issues/7", results);
        results.Should().ContainSingle().Which.Should().Be("7");
    }
}
