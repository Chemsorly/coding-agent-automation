using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for IssueReferenceParser — validates extraction of issue references
/// from PR/MR title and description text.
/// </summary>
public class IssueReferenceParserTests
{
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
}
