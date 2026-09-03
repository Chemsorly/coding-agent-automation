using Bunit;
using CodingAgentWebUI.Components.Shared;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for <see cref="FeedbackSection"/>. Previously these rendered the section
/// through the (now retired) AgentMonitoring page and its run-detail modal; they now render the
/// standalone component directly with a <c>Feedback</c> parameter — the same coverage without the
/// host-page coupling. FeedbackSection is used by the RunPage (/runs/{id}) live/detail view.
/// Validates Requirements 5.1–5.4.
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

    private IRenderedComponent<FeedbackSection> RenderFeedback(RunFeedback? feedback)
        => Render<FeedbackSection>(ps => ps.Add(p => p.Feedback, feedback));

    /// <summary>Requirement 5.1: Feedback section renders when Feedback is non-null.</summary>
    [Fact]
    public void FeedbackSection_Renders_WhenFeedbackIsNonNull()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        Assert.NotEmpty(cut.FindAll(".feedback-section"));
    }

    /// <summary>Requirement 5.4: Feedback section hidden when Feedback is null.</summary>
    [Fact]
    public void FeedbackSection_Hidden_WhenFeedbackIsNull()
    {
        var cut = RenderFeedback(null);
        Assert.Empty(cut.FindAll(".feedback-section"));
    }

    /// <summary>Requirement 5.2: Harness feedback fields display correctly — Category badge.</summary>
    [Fact]
    public void HarnessFeedback_DisplaysCategoryBadge()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var badge = cut.Find(".feedback-category-badge");
        Assert.Contains("mcp tool timeout", badge.TextContent);
    }

    /// <summary>Requirement 5.2: Harness feedback fields display correctly — StuckReason.</summary>
    [Fact]
    public void HarnessFeedback_DisplaysStuckReason()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var stuckReason = cut.Find(".feedback-stuck-reason");
        Assert.Contains("The MCP server was unreachable after 3 retries", stuckReason.TextContent);
    }

    /// <summary>Requirement 5.2: List fields render as bullet points (ul/li elements).</summary>
    [Fact]
    public void HarnessFeedback_ListsRenderAsBulletPoints()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var listSections = cut.FindAll(".feedback-list-section");
        Assert.True(listSections.Count >= 4, "Expected at least 4 list sections (MissingContext, MissingCapabilities, PromptIssues, Suggestions)");
        foreach (var section in listSections)
        {
            var listItems = section.QuerySelectorAll("ul li");
            Assert.NotEmpty(listItems);
        }
    }

    /// <summary>Requirement 5.2: MissingContext items display correctly.</summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingContextItems()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var markup = cut.Markup;
        Assert.Contains("src/Config.cs", markup);
        Assert.Contains("docs/setup.md", markup);
    }

    /// <summary>Requirement 5.2: MissingCapabilities items display correctly.</summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingCapabilitiesItems()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var markup = cut.Markup;
        Assert.Contains("database access", markup);
        Assert.Contains("network diagnostics", markup);
    }

    /// <summary>Requirement 5.3: Issue feedback section hidden when Issue is null.</summary>
    [Fact]
    public void IssueFeedback_Hidden_WhenIssueIsNull()
    {
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
        var cut = RenderFeedback(feedbackWithoutIssue);

        Assert.NotEmpty(cut.FindAll(".feedback-section"));
        var subsections = cut.FindAll(".feedback-subsection");
        Assert.Single(subsections);
        Assert.Contains("Harness Feedback", subsections[0].TextContent);
    }

    /// <summary>Requirement 5.3: Issue feedback displays Description.</summary>
    [Fact]
    public void IssueFeedback_DisplaysDescription()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var description = cut.Find(".feedback-description");
        Assert.Contains("The referenced UserService class does not exist", description.TextContent);
    }

    /// <summary>Requirement 5.3: Issue feedback displays AffectedFiles as a list.</summary>
    [Fact]
    public void IssueFeedback_DisplaysAffectedFilesAsList()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var markup = cut.Markup;
        Assert.Contains("src/Services/UserService.cs", markup);
        Assert.Contains("src/Controllers/UserController.cs", markup);
    }

    /// <summary>Requirement 5.3: Issue feedback displays HumanActionNeeded.</summary>
    [Fact]
    public void IssueFeedback_DisplaysHumanActionNeeded()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var actionNeeded = cut.Find(".feedback-action-needed");
        Assert.Contains("Create the UserService class or update the issue", actionNeeded.TextContent);
    }

    /// <summary>Requirement 5.3: Issue feedback displays Category badge (harness + issue = two badges).</summary>
    [Fact]
    public void IssueFeedback_DisplaysCategoryBadge()
    {
        var cut = RenderFeedback(CreateFullFeedback());
        var badges = cut.FindAll(".feedback-category-badge");
        Assert.Equal(2, badges.Count);
        Assert.Contains("missing component", badges[1].TextContent);
    }

    /// <summary>Requirement 5.2: Harness feedback with empty lists does not render list sections.</summary>
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
        var cut = RenderFeedback(minimalFeedback);

        Assert.NotEmpty(cut.FindAll(".feedback-section"));
        Assert.Empty(cut.FindAll(".feedback-list-section"));
    }

    /// <summary>Requirement 5.2: Harness feedback without StuckReason does not render stuck reason div.</summary>
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
        var cut = RenderFeedback(feedbackNoStuck);
        Assert.Empty(cut.FindAll(".feedback-stuck-reason"));
    }
}
