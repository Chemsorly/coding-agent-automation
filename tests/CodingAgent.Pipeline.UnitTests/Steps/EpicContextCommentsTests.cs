using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Services.Steps;
using System.Text;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Tests for <see cref="EpicContextComments"/>, which writes the comments of an epic into its context file for
/// both decomposition steps: verified comments as they are, unverified ones normalized under "Unverified input".
/// </summary>
public class EpicContextCommentsTests
{
    private static string Heading(IssueComment comment) => $"### {comment.Author}";

    private static IssueComment Comment(string author, string body, bool? isVerified) => new()
    {
        Id = author,
        Author = author,
        Body = body,
        CreatedAt = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
        IsVerified = isVerified
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Append_NoComments_WritesNothingAndReportsNoUnverifiedComments(bool divider)
    {
        var sb = new StringBuilder();

        var hasUnverified = EpicContextComments.Append(sb, [], Heading, divider);

        hasUnverified.Should().BeFalse();
        sb.ToString().Should().BeEmpty();
    }

    [Fact]
    public void Append_VerifiedAndNullComments_AreWrittenRawUnderComments()
    {
        var sb = new StringBuilder();
        var comments = new[]
        {
            Comment("maintainer", "Keep <!-- note --> ​this.", isVerified: true),
            Comment("older-api", "Same <!-- note -->.", isVerified: null)
        };

        var hasUnverified = EpicContextComments.Append(sb, comments, Heading, dividerBeforeComments: false);

        hasUnverified.Should().BeFalse();
        sb.ToString().ReplaceLineEndings("\n").Should().Be(
            "## Comments\n\n### maintainer\n\nKeep <!-- note --> ​this.\n\n### older-api\n\nSame <!-- note -->.\n\n");
    }

    [Fact]
    public void Append_DividerBeforeComments_PutsARuleAboveTheCommentsHeading()
    {
        var sb = new StringBuilder();

        EpicContextComments.Append(sb, [Comment("maintainer", "Hello.", isVerified: true)], Heading, dividerBeforeComments: true);

        sb.ToString().ReplaceLineEndings("\n").Should().Be("---\n\n## Comments\n\n### maintainer\n\nHello.\n\n");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Append_OnlyUnverifiedComments_WritesNoCommentsHeadingAndNoDivider(bool divider)
    {
        var sb = new StringBuilder();

        var hasUnverified = EpicContextComments.Append(
            sb, [Comment("outsider", "Visible.<!-- hidden -->​", isVerified: false)], Heading, divider);

        hasUnverified.Should().BeTrue();
        sb.ToString().ReplaceLineEndings("\n").Should().Be(
            UnverifiedInputText.SectionHeading + "\n\n" + UnverifiedInputText.SectionRule + "\n\n### outsider\n\nVisible.\n\n");
    }

    [Fact]
    public void Append_BothKinds_KeepsOrderWithinEachGroupAndPutsUnverifiedLast()
    {
        var sb = new StringBuilder();
        var comments = new[]
        {
            Comment("outsider-1", "First outside.", isVerified: false),
            Comment("maintainer-1", "First inside.", isVerified: true),
            Comment("outsider-2", "Second outside.", isVerified: false),
            Comment("maintainer-2", "Second inside.", isVerified: true)
        };

        var hasUnverified = EpicContextComments.Append(sb, comments, Heading, dividerBeforeComments: false);

        hasUnverified.Should().BeTrue();
        sb.ToString().ReplaceLineEndings("\n").Should().Be(
            "## Comments\n\n### maintainer-1\n\nFirst inside.\n\n### maintainer-2\n\nSecond inside.\n\n"
            + UnverifiedInputText.SectionHeading + "\n\n" + UnverifiedInputText.SectionRule
            + "\n\n### outsider-1\n\nFirst outside.\n\n### outsider-2\n\nSecond outside.\n\n");
    }
}
