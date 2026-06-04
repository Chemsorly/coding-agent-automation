using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="PrConversationContextFormatter"/>.
/// Covers formatting logic, author attribution, and empty state.
/// </summary>
public class PrConversationContextFormatterTests
{
    [Fact]
    public void Format_EmptyComments_ContainsNoConversationMessage()
    {
        var result = PrConversationContextFormatter.Format(Array.Empty<PrConversationComment>());

        result.Should().Contain("# PR Conversation Context");
        result.Should().Contain("No prior conversation or review comments found.");
    }

    [Fact]
    public void Format_DiscussionComment_ShowsInDiscussionSection()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "alice",
                CreatedAt = new DateTime(2026, 6, 1, 14, 30, 0, DateTimeKind.Utc),
                Body = "This looks good overall.",
                IsBot = false,
                IsAuthor = false,
                FilePath = null,
                Line = null,
                IsResolved = null
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("## Discussion Comments");
        result.Should().Contain("[HUMAN] @alice (2026-06-01 14:30 UTC)");
        result.Should().Contain("This looks good overall.");
    }

    [Fact]
    public void Format_BotComment_ShowsBotAttribution()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "pipeline-bot",
                CreatedAt = new DateTime(2026, 6, 1, 15, 0, 0, DateTimeKind.Utc),
                Body = "Analysis complete.",
                IsBot = true,
                IsAuthor = false,
                FilePath = null,
                Line = null,
                IsResolved = null
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("[BOT] @pipeline-bot (2026-06-01 15:00 UTC)");
    }

    [Fact]
    public void Format_AuthorComment_ShowsHumanAuthorAttribution()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "pr-author",
                CreatedAt = new DateTime(2026, 6, 1, 16, 0, 0, DateTimeKind.Utc),
                Body = "Fixed in commit abc1234.",
                IsBot = false,
                IsAuthor = true,
                FilePath = null,
                Line = null,
                IsResolved = null
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("[HUMAN/AUTHOR] @pr-author (2026-06-01 16:00 UTC)");
    }

    [Fact]
    public void Format_ReviewThreadComment_ShowsInReviewThreadSection()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "reviewer",
                CreatedAt = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc),
                Body = "Null reference risk here.",
                IsBot = false,
                IsAuthor = false,
                FilePath = "src/Service.cs",
                Line = 45,
                IsResolved = false
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("## Review Thread Comments");
        result.Should().Contain("[HUMAN] @reviewer");
        result.Should().Contain("src/Service.cs:45");
        result.Should().Contain("Null reference risk here.");
    }

    [Fact]
    public void Format_ResolvedReviewThread_ShowsResolvedMarker()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "bot",
                CreatedAt = new DateTime(2026, 6, 2, 10, 0, 0, DateTimeKind.Utc),
                Body = "Issue fixed.",
                IsBot = true,
                IsAuthor = false,
                FilePath = "src/Handler.cs",
                Line = 12,
                IsResolved = true
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("(RESOLVED)");
    }

    [Fact]
    public void Format_MixedComments_SeparatesIntoSections()
    {
        var comments = new List<PrConversationComment>
        {
            new()
            {
                Author = "human",
                CreatedAt = new DateTime(2026, 6, 1, 10, 0, 0, DateTimeKind.Utc),
                Body = "Discussion comment",
                IsBot = false,
                IsAuthor = false,
                FilePath = null,
                Line = null,
                IsResolved = null
            },
            new()
            {
                Author = "bot",
                CreatedAt = new DateTime(2026, 6, 1, 11, 0, 0, DateTimeKind.Utc),
                Body = "Review finding",
                IsBot = true,
                IsAuthor = false,
                FilePath = "src/X.cs",
                Line = 5,
                IsResolved = false
            }
        };

        var result = PrConversationContextFormatter.Format(comments);

        result.Should().Contain("## Discussion Comments");
        result.Should().Contain("## Review Thread Comments");
    }

    [Fact]
    public void FormatAttribution_Bot_ReturnsBotTag()
    {
        var comment = new PrConversationComment
        {
            Author = "bot", CreatedAt = DateTime.UtcNow, Body = "", IsBot = true, IsAuthor = false
        };

        PrConversationContextFormatter.FormatAttribution(comment).Should().Be("[BOT]");
    }

    [Fact]
    public void FormatAttribution_Author_ReturnsHumanAuthorTag()
    {
        var comment = new PrConversationComment
        {
            Author = "author", CreatedAt = DateTime.UtcNow, Body = "", IsBot = false, IsAuthor = true
        };

        PrConversationContextFormatter.FormatAttribution(comment).Should().Be("[HUMAN/AUTHOR]");
    }

    [Fact]
    public void FormatAttribution_RegularHuman_ReturnsHumanTag()
    {
        var comment = new PrConversationComment
        {
            Author = "reviewer", CreatedAt = DateTime.UtcNow, Body = "", IsBot = false, IsAuthor = false
        };

        PrConversationContextFormatter.FormatAttribution(comment).Should().Be("[HUMAN]");
    }

    [Fact]
    public void Format_NullComments_ThrowsArgumentNullException()
    {
        var action = () => PrConversationContextFormatter.Format(null!);
        action.Should().Throw<ArgumentNullException>();
    }
}
