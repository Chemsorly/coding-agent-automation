using Bunit;
using CodingAgent.Web.Components.Shared;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="FeedbackSection" />.
/// Validates Requirements 5.1, 5.2, 5.3, 5.4.
/// FeedbackSection has no DI dependencies — tests mount it directly.
/// </summary>
public class FeedbackSectionComponentTests : BunitContext
{
    private static RunFeedback CreateFullFeedback()
    {
        return new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "mcp tool timeout",
                StuckReason = "The MCP server was unreachable after 3 retries",
                MissingContext = ["src/Config.cs", "docs/setup.md"],
                MissingCapabilities = ["database access", "network diagnostics"],
                PromptIssues = ["contradictory instructions about error handling"],
                Suggestions = ["add retry logic to MCP calls", "provide fallback config"]
            },
            Issue = new IssueFeedback
            {
                Category = "missing component",
                Description = "The referenced UserService class does not exist in the repository",
                AffectedFiles = ["src/Services/UserService.cs", "src/Controllers/UserController.cs"],
                HumanActionNeeded = "Create the UserService class or update the issue to reference the correct service"
            }
        };
    }

    /// <summary>
    /// Requirement 5.1: Feedback section renders when Feedback is non-null.
    /// </summary>
    [Fact]
    public void FeedbackSection_Renders_WhenFeedbackIsNonNull()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var feedbackSections = cut.FindAll(".feedback-section");
        Assert.NotEmpty(feedbackSections);
    }

    /// <summary>
    /// Requirement 5.4: Feedback section hidden when Feedback is null.
    /// </summary>
    [Fact]
    public void FeedbackSection_Hidden_WhenFeedbackIsNull()
    {
        // TODO: Use explicit cast `(RunFeedback?)null` to guarantee null is forwarded rather than
        // relying on the compiler to infer it. Without the cast, if Feedback is non-nullable,
        // the null may be silently treated as a no-op and the component could receive a default value.
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, null));

        var feedbackSections = cut.FindAll(".feedback-section");
        Assert.Empty(feedbackSections);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback fields display correctly — Category badge.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysCategoryBadge()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var badge = cut.Find(".feedback-category-badge");
        Assert.Contains("mcp tool timeout", badge.TextContent);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback fields display correctly — StuckReason.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysStuckReason()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var stuckReason = cut.Find(".feedback-stuck-reason");
        Assert.Contains("The MCP server was unreachable after 3 retries", stuckReason.TextContent);
    }

    /// <summary>
    /// Requirement 5.2: List fields render as bullet points (ul/li elements).
    /// </summary>
    [Fact]
    public void HarnessFeedback_ListsRenderAsBulletPoints()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var listSections = cut.FindAll(".feedback-list-section");
        // TODO: Use Assert.Equal(4, listSections.Count) instead of >= 4 to pin the exact count
        // and catch regressions that add duplicate or unexpected list sections.
        Assert.True(listSections.Count >= 4, "Expected at least 4 list sections (MissingContext, MissingCapabilities, PromptIssues, Suggestions)");

        // Verify each list section contains ul > li elements
        foreach (var section in listSections)
        {
            var listItems = section.QuerySelectorAll("ul li");
            Assert.NotEmpty(listItems);
        }
    }

    /// <summary>
    /// Requirement 5.2: MissingContext items display correctly.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingContextItems()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        // TODO: Scope assertions to the specific list section element (e.g. cut.Find(".feedback-list-section"))
        // rather than cut.Markup to prevent false passes if the text appears in an unrelated element
        // (tooltip, title attribute, or a different list section).
        var markup = cut.Markup;
        Assert.Contains("src/Config.cs", markup);
        Assert.Contains("docs/setup.md", markup);
    }

    /// <summary>
    /// Requirement 5.2: MissingCapabilities items display correctly.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingCapabilitiesItems()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var markup = cut.Markup;
        Assert.Contains("database access", markup);
        Assert.Contains("network diagnostics", markup);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback section hidden when Issue is null.
    /// </summary>
    [Fact]
    public void IssueFeedback_Hidden_WhenIssueIsNull()
    {
        // TODO: This test uses FeedbackOutcome.Success for the Harness feedback. If the component
        // conditionally suppresses Harness rendering for Success outcomes, the single-subsection
        // assertion would give a misleading failure. Consider using CreateFullFeedback() with
        // Issue = null (Failure outcome) to remove this ambiguity, or add a comment explaining
        // why Success is intentional here.
        var feedbackWithoutIssue = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "slow build",
                Suggestions = ["cache dependencies"]
            },
            Issue = null
        };
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, feedbackWithoutIssue));

        // Feedback section should exist
        Assert.NotEmpty(cut.FindAll(".feedback-section"));

        // But only one subsection (Harness), not two
        var subsections = cut.FindAll(".feedback-subsection");
        Assert.Single(subsections);
        Assert.Contains("Harness Feedback", subsections[0].TextContent);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays Description.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysDescription()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var description = cut.Find(".feedback-description");
        Assert.Contains("The referenced UserService class does not exist", description.TextContent);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays AffectedFiles as a list.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysAffectedFilesAsList()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var markup = cut.Markup;
        Assert.Contains("src/Services/UserService.cs", markup);
        Assert.Contains("src/Controllers/UserController.cs", markup);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays HumanActionNeeded.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysHumanActionNeeded()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var actionNeeded = cut.Find(".feedback-action-needed");
        Assert.Contains("Create the UserService class or update the issue", actionNeeded.TextContent);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays Category badge.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysCategoryBadge()
    {
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, CreateFullFeedback()));

        var badges = cut.FindAll(".feedback-category-badge");
        // Should have two badges: one for harness, one for issue
        Assert.Equal(2, badges.Count);
        // TODO: Replace positional index `badges[1]` with a scoped selector that finds the badge
        // within the Issue subsection specifically. If the component renders Issue before Harness,
        // badges[1] would be the wrong badge and the assertion would silently pass or fail incorrectly.
        Assert.Contains("missing component", badges[1].TextContent);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback with empty lists does not render list sections.
    /// </summary>
    [Fact]
    public void HarnessFeedback_EmptyLists_DoNotRenderListSections()
    {
        var minimalFeedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "clean run"
                // All lists default to empty
            },
            Issue = null
        };
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, minimalFeedback));

        // Feedback section exists
        Assert.NotEmpty(cut.FindAll(".feedback-section"));

        // No list sections rendered (all lists are empty)
        var listSections = cut.FindAll(".feedback-list-section");
        Assert.Empty(listSections);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback without StuckReason does not render stuck reason div.
    /// </summary>
    [Fact]
    public void HarnessFeedback_NoStuckReason_DoesNotRenderStuckReasonDiv()
    {
        var feedbackNoStuck = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "clean run",
                StuckReason = null
            },
            Issue = null
        };
        var cut = Render<FeedbackSection>(p => p.Add(x => x.Feedback, feedbackNoStuck));

        Assert.Empty(cut.FindAll(".feedback-stuck-reason"));
    }
}
