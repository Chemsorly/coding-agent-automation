using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using CodingAgent.Web.UnitTests.Auth;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for Knowledge page (spec 054 8B.2 and issue #2939):
/// - New v2 tiles (Runs · recent, Skills injected, Avg skills size, Inbox pushed, Inbox push failures)
/// - "Does the brain help?" comparison uses BrainSkillsBytes > 0 as "with skills"
/// - Second API call for global readers only
/// - Link to /consolidation
/// </summary>
public class KnowledgePageTests : BunitContext
{
    private readonly Mock<CodingAgent.Web.Ui.IUiRunClient> _mockHistory = new();

    private static PagedResult<PipelineRunSummary> PagedHistory(params PipelineRunSummary[] items) => new()
    {
        Items = items.ToList(),
        Page = 1,
        PageSize = 100,
        HasMore = false
    };

    /// <summary>
    /// Creates a non-consolidation run.
    /// <paramref name="skillsBytes"/> &gt; 0 means "with skills" in the v2 comparison.
    /// </summary>
    private static PipelineRunSummary MakeRun(string id, int skillsBytes = 0, bool completed = true) => new()
    {
        RunId = id,
        IssueIdentifier = "1",
        IssueTitle = "Test",
        RunType = PipelineRunType.Implementation,
        FinalStep = completed ? PipelineStep.Completed : PipelineStep.Failed,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
        BrainRepoUsed = skillsBytes > 0,
        BrainSkillsBytes = skillsBytes > 0 ? skillsBytes : null,
        InitiatedBy = "manual",
    };

    public KnowledgePageTests()
    {
        Services.AddSingleton(_mockHistory.Object);
        Services.AddSingleton(new CockpitState());
        // Default: project-scoped user (no global read) — tests that need global access call AddTestAccess again
        Services.AddTestAccess(TestAccess.Scoped(("project-1", AccessRole.ReadOnly)));
    }

    [Fact]
    public void Knowledge_HidesBrainComparisonBars_WhenNoSkilllessRuns()
    {
        // All runs have skills — no "without skills" group to compare against
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(
                MakeRun("r1", skillsBytes: 1024),
                MakeRun("r2", skillsBytes: 2048)));

        var cut = Render<Knowledge>();

        // The placeholder message for "needs both groups" must appear
        cut.Markup.Should().Contain("without",
            "when all runs got skills, the placeholder message about needing comparison data must appear");

        // Must NOT show the percentage-lift text which only appears when both groups are non-empty
        cut.Markup.Should().NotContain("completed-rate on runs",
            "the completed-rate lift must be hidden when there are no skill-less runs to compare against");
    }

    [Fact]
    public void Knowledge_ShowsBrainComparisonBars_WhenBothGroupsHaveRuns()
    {
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(
                MakeRun("r1", skillsBytes: 1024),
                MakeRun("r2", skillsBytes: 0)));

        var cut = Render<Knowledge>();

        // When both groups exist the comparison bars + lift text appear
        cut.Markup.Should().Contain("With skills",
            "the 'With skills' bar must appear when both skills and no-skills runs exist (spec 054 8B.2)");
        cut.Markup.Should().Contain("Without skills",
            "the 'Without skills' bar must appear when both groups exist (spec 054 8B.2)");
    }

    [Fact]
    public void Knowledge_LinksToBrainConsolidationPage()
    {
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", skillsBytes: 1024)));

        var cut = Render<Knowledge>();

        // The info card must contain a link to /consolidation (not /pipelines)
        var consolidationLink = cut.FindAll("a")
            .FirstOrDefault(a => a.GetAttribute("href") == "/consolidation");

        consolidationLink.Should().NotBeNull(
            "the Knowledge info card must link to /consolidation for brain consolidation");
    }

    [Fact]
    public void Knowledge_DoesNotLinkToPipelines_InInfoCard()
    {
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", skillsBytes: 1024)));

        var cut = Render<Knowledge>();

        // Must not contain the old /pipelines href
        var pipelinesLinks = cut.FindAll("a[href='pipelines'], a[href='/pipelines']");
        pipelinesLinks.Should().BeEmpty(
            "the page must not link to /pipelines — it now points to /consolidation");
    }

    [Fact]
    public void Knowledge_DoesNotShowDeveloperJargon()
    {
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", skillsBytes: 1024)));

        var cut = Render<Knowledge>();

        cut.Markup.Should().NotContain("per-file is fabricated",
            "developer-facing implementation language must be replaced with operator-facing copy");
    }

    [Fact]
    public void Knowledge_GlobalReader_MakesSecondCallForConsolidation()
    {
        // Spec 054 8B.2: a second API call is made for global readers only.
        // Override the constructor's scoped access with global read.
        Services.AddTestAccess(TestAccess.Global(AccessRole.ReadOnly));
        // Non-empty first call so the code doesn't return early.
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", skillsBytes: 1024)));

        _ = Render<Knowledge>();

        // Two calls: one for non-consolidation runs, one for consolidation (global reader path)
        _mockHistory.Verify(
            c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()),
            Times.Exactly(2),
            "a global reader causes a second history call for brain consolidation runs");
    }

    [Fact]
    public void Knowledge_ProjectScopedUser_DoesNotMakeSecondCallForConsolidation()
    {
        // Spec 054 8B.2: no second call for project-scoped users (would 403).
        // The constructor already set project-scoped access. Provide runs so we don't return early.
        _mockHistory.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(PagedHistory(MakeRun("r1", skillsBytes: 1024)));

        _ = Render<Knowledge>();

        // Only one call (project-scoped: no global access, so no second consolidation call)
        _mockHistory.Verify(
            c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "a project-scoped user must not trigger the second history call (would 403)");
    }
}
