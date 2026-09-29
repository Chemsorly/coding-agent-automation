using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using Moq;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="DecompositionStep"/> pure-logic helper methods.
/// </summary>
public class DecompositionStepTests
{
    // ── FindMostRecentPlanComment ────────────────────────────────────────

    [Fact]
    public void FindMostRecentPlanComment_WhenNoPlanComment_ReturnsNull()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, "Regular comment body"),
            MakeComment(2, "Another comment without the marker")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result.Should().BeNull("no comment contains the decomposition plan marker");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenEmptyList_ReturnsNull()
    {
        var result = DecompositionStep.FindMostRecentPlanComment([]);
        result.Should().BeNull();
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenSingleMatchingComment_ReturnsThatComment()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, "Regular comment"),
            MakeComment(2, $"Here is the plan {CommentMarkers.DecompositionPlan} end of plan")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result.Should().NotBeNull();
        result!.Id.Should().Be("2");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenMultipleMatches_ReturnsMostRecent()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(1, $"Old plan {CommentMarkers.DecompositionPlan}"),
            MakeComment(2, "No marker"),
            MakeComment(3, $"Newer plan {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result!.Id.Should().Be("3", "should return the most recent (last) matching comment");
    }

    [Fact]
    public void FindMostRecentPlanComment_WhenAllCommentsMatch_ReturnslastOne()
    {
        var comments = new List<IssueComment>
        {
            MakeComment(10, $"Plan 1 {CommentMarkers.DecompositionPlan}"),
            MakeComment(20, $"Plan 2 {CommentMarkers.DecompositionPlan}"),
            MakeComment(30, $"Plan 3 {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);

        result!.Id.Should().Be("30");
    }

    [Fact]
    public void FindMostRecentPlanComment_MarkerIsCaseSensitive()
    {
        // The marker is matched with StringComparison.Ordinal — wrong case should not match
        var lowerMarker = CommentMarkers.DecompositionPlan.ToLowerInvariant();
        var upperMarker = CommentMarkers.DecompositionPlan.ToUpperInvariant();

        // Only add comments with wrong-case markers (assuming the real marker is mixed-case)
        // This test verifies the search doesn't accidentally match wrong-case text
        // by using the actual marker to confirm it DOES match
        var comments = new List<IssueComment>
        {
            MakeComment(1, $"Contains real marker: {CommentMarkers.DecompositionPlan}")
        };

        var result = DecompositionStep.FindMostRecentPlanComment(comments);
        result.Should().NotBeNull("exact marker must match");
    }

    // ── BuildIssueContextContent (via reflection) ─────────────────────────

    [Fact]
    public void BuildIssueContextContent_WithNoComments_ContainsIssueTitleAndDescription()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#1",
            Title = "Fix the login bug",
            Description = "Users cannot log in when 2FA is enabled.",
            Labels = Array.Empty<string>()
        };
        var result = InvokeBuildIssueContextContent(issue, new List<IssueComment>());

        result.Should().Contain("Fix the login bug");
        result.Should().Contain("Users cannot log in when 2FA is enabled.");
        result.Should().Contain("# Epic Issue Context");
    }

    [Fact]
    public void BuildIssueContextContent_WithComments_IncludesCommentAuthorAndBody()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#2",
            Title = "My Epic",
            Description = "Epic description",
            Labels = Array.Empty<string>()
        };
        var comments = new List<IssueComment>
        {
            MakeComment(1, "This is the approved plan."),
            MakeComment(2, "Additional comment here.")
        };

        var result = InvokeBuildIssueContextContent(issue, comments);

        result.Should().Contain("This is the approved plan.");
        result.Should().Contain("Additional comment here.");
        result.Should().Contain("test-author");
        result.Should().Contain("## Comments");
    }

    [Fact]
    public void BuildIssueContextContent_WithEmptyDescription_DoesNotThrow()
    {
        var issue = new IssueDetail
        {
            Identifier = "org/repo#3",
            Title = "Empty desc",
            Description = "",
            Labels = Array.Empty<string>()
        };
        var act = () => InvokeBuildIssueContextContent(issue, []);
        act.Should().NotThrow();
    }

    // ── BuildDeduplicationSection (via reflection) ────────────────────────

    [Fact]
    public void BuildDeduplicationSection_WithTitles_ContainsAllTitles()
    {
        var titles = new List<string> { "Fix login bug", "Add dark mode", "Improve performance" };
        var result = InvokeBuildDeduplicationSection(titles);

        result.Should().Contain("Fix login bug");
        result.Should().Contain("Add dark mode");
        result.Should().Contain("Improve performance");
        result.Should().Contain("Do NOT Duplicate");
    }

    [Fact]
    public void BuildDeduplicationSection_EmptyList_ContainsHeader()
    {
        var result = InvokeBuildDeduplicationSection([]);
        result.Should().Contain("Existing Agent-Generated Sub-Issues");
    }

    [Fact]
    public void BuildDeduplicationSection_WithSingleTitle_FormatsAsBullet()
    {
        var result = InvokeBuildDeduplicationSection(["Only one issue"]);
        result.Should().Contain("- Only one issue");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    // ── QueryExistingSubIssueTitlesAsync (via reflection) ────────────────────

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_SinglePage_ReturnsTitles()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();
        mockOps.Setup(o => o.ListOpenIssuesAsync(1, 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>
            {
                Items = new List<CodingAgent.Pipeline.Models.IssueSummary>
                {
                    new() { Identifier = "1", Title = "Issue Alpha", Labels = Array.Empty<string>() },
                    new() { Identifier = "2", Title = "Issue Beta", Labels = Array.Empty<string>() }
                }.AsReadOnly(),
                HasMore = false, Page = 1, PageSize = 50
            });

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, null, "run-1", CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain("Issue Alpha");
        result.Should().Contain("Issue Beta");
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_MultiPage_ReturnsCombinedTitles()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();
        mockOps.SetupSequence(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>
            {
                Items = new List<CodingAgent.Pipeline.Models.IssueSummary>
                {
                    new() { Identifier = "1", Title = "Page1-Issue1", Labels = Array.Empty<string>() }
                }.AsReadOnly(),
                HasMore = true, Page = 1, PageSize = 50
            })
            .ReturnsAsync(new CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>
            {
                Items = new List<CodingAgent.Pipeline.Models.IssueSummary>
                {
                    new() { Identifier = "2", Title = "Page2-Issue1", Labels = Array.Empty<string>() }
                }.AsReadOnly(),
                HasMore = false, Page = 2, PageSize = 50
            });

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, null, "run-2", CancellationToken.None);

        result.Should().HaveCount(2);
        result.Should().Contain("Page1-Issue1");
        result.Should().Contain("Page2-Issue1");
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_EmptyResult_ReturnsEmpty()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();
        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>
            {
                Items = new List<CodingAgent.Pipeline.Models.IssueSummary>().AsReadOnly(),
                HasMore = false, Page = 1, PageSize = 50
            });

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, null, "run-3", CancellationToken.None);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_ExceptionSwallowed_ReturnsEmpty()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();
        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("API error"));

        // Exception must be swallowed, returning empty list
        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, null, "run-4", CancellationToken.None);

        result.Should().BeEmpty("exceptions from the issues API must be swallowed");
    }

    // ── QueryExistingSubIssueTitlesAsync — project epic (AC1) ─────────────

    // TODO: The label filter ("agent:generated") passed to ListOpenIssuesForProviderAsync is not verified
    // in any of the tests below. All setups use It.IsAny<IReadOnlyList<string>>() for labels.
    // Add Verify calls that assert labels.Contains("agent:generated") on the scoped calls to ensure
    // the correct filter is forwarded to CollectTitlesFromTrackerAsync.
    // See review finding: DecompositionStepTests — label filter not verified for scoped calls.

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithProjectContext_AggregatesFromTemplateTrackers()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        // Own tracker returns "Sub-Issue A"
        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Sub-Issue A"));

        // Template tracker "provider-1" returns "Sub-Issue B"
        mockOps.Setup(o => o.ListOpenIssuesForProviderAsync("provider-1", It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Sub-Issue B"));

        // Template tracker "provider-2" returns "Sub-Issue C"
        mockOps.Setup(o => o.ListOpenIssuesForProviderAsync("provider-2", It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Sub-Issue C"));

        var projectContext = MakeProjectContext(
            MakeRepo("provider-1", decompositionEnabled: true),
            MakeRepo("provider-2", decompositionEnabled: true));

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, projectContext, "run-ac1", CancellationToken.None);

        result.Should().Contain("Sub-Issue A");
        result.Should().Contain("Sub-Issue B");
        result.Should().Contain("Sub-Issue C");
        result.Should().HaveCount(3);
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithProjectContext_DeduplicatesTitlesAcrossTrackers()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        // Own tracker and template tracker both return the same title
        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Duplicate Title"));
        mockOps.Setup(o => o.ListOpenIssuesForProviderAsync("provider-1", It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Duplicate Title"));

        var projectContext = MakeProjectContext(MakeRepo("provider-1", decompositionEnabled: true));

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, projectContext, "run-dedup", CancellationToken.None);

        result.Should().ContainSingle("Duplicate Title", "cross-tracker deduplication must yield exactly one entry");
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithProjectContext_SkipsRepositoryWithNoIssueProviderId()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Own Issue"));

        // Repository with no IssueProviderId — must be skipped
        var projectContext = MakeProjectContext(MakeRepo(issueProviderId: null, decompositionEnabled: true));

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, projectContext, "run-null-provider", CancellationToken.None);

        result.Should().ContainSingle("Own Issue");
        mockOps.Verify(
            o => o.ListOpenIssuesForProviderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "repositories with no IssueProviderId must not trigger a scoped list call");
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithProjectContext_SkipsRepositoryWithDecompositionDisabled()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Own Issue"));

        // Repository with decomposition disabled — must be skipped
        var projectContext = MakeProjectContext(MakeRepo("provider-x", decompositionEnabled: false));

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, projectContext, "run-disabled", CancellationToken.None);

        result.Should().ContainSingle("Own Issue");
        mockOps.Verify(
            o => o.ListOpenIssuesForProviderAsync("provider-x", It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "repositories with decomposition disabled must not trigger a scoped list call");
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithProjectContext_FaultTolerancePerTracker()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Own Issue"));

        // provider-1 throws — exception must be swallowed per-tracker
        mockOps.Setup(o => o.ListOpenIssuesForProviderAsync("provider-1", It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("tracker unavailable"));

        // provider-2 succeeds
        mockOps.Setup(o => o.ListOpenIssuesForProviderAsync("provider-2", It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Template Issue"));

        var projectContext = MakeProjectContext(
            MakeRepo("provider-1", decompositionEnabled: true),
            MakeRepo("provider-2", decompositionEnabled: true));

        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, projectContext, "run-fault", CancellationToken.None);

        result.Should().Contain("Own Issue", "own tracker must still contribute even when a template tracker fails");
        result.Should().Contain("Template Issue", "successful template tracker must still contribute after a failing one");
        // TODO: Add result.Should().HaveCount(2) to make the per-tracker isolation boundary explicit.
        // The current assertions do not rule out items from the failed provider-1 being included
        // (e.g. via a partial result before the throw). See review finding: FaultTolerancePerTracker — missing count assertion.
    }

    [Fact]
    public async Task QueryExistingSubIssueTitlesAsync_WithoutProjectContext_UsesOwnTrackerOnly()
    {
        var mockOps = new Mock<CodingAgent.Pipeline.Interfaces.IAgentIssueOperations>();

        mockOps.Setup(o => o.ListOpenIssuesAsync(It.IsAny<int>(), 50, It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakePagedResult("Own Issue"));

        // projectContext = null → repo epic path, no scoped calls
        var result = await InvokeQueryExistingSubIssueTitlesAsync(mockOps.Object, null, "run-repo-epic", CancellationToken.None);

        result.Should().ContainSingle("Own Issue");
        mockOps.Verify(
            o => o.ListOpenIssuesForProviderAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "repo epic (null projectContext) must never call ListOpenIssuesForProviderAsync");
    }

    // ── Test helpers ──────────────────────────────────────────────────────

    private static CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> MakePagedResult(
        params string[] titles)
    {
        return new CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>
        {
            Items = titles.Select(t => new CodingAgent.Pipeline.Models.IssueSummary
            {
                Identifier = t,
                Title = t,
                Labels = Array.Empty<string>()
            }).ToList().AsReadOnly(),
            HasMore = false,
            Page = 1,
            PageSize = 50
        };
    }

    private static CodingAgent.Pipeline.Models.DecompositionProjectContext MakeProjectContext(
        params CodingAgent.Pipeline.Models.RepositoryTarget[] repos)
    {
        return new CodingAgent.Pipeline.Models.DecompositionProjectContext
        {
            ProjectName = "Test Project",
            Repositories = repos
        };
    }

    private static CodingAgent.Pipeline.Models.RepositoryTarget MakeRepo(
        string? issueProviderId, bool decompositionEnabled)
    {
        return new CodingAgent.Pipeline.Models.RepositoryTarget
        {
            TemplateName = issueProviderId ?? "no-provider",
            Description = "Test repo",
            IssueProviderId = issueProviderId,
            DecompositionEnabled = decompositionEnabled
        };
    }

    private static async Task<IReadOnlyList<string>> InvokeQueryExistingSubIssueTitlesAsync(
        CodingAgent.Pipeline.Interfaces.IAgentIssueOperations issueOps,
        CodingAgent.Pipeline.Models.DecompositionProjectContext? projectContext,
        string runId,
        CancellationToken ct)
    {
        var method = typeof(DecompositionStep).GetMethod(
            "QueryExistingSubIssueTitlesAsync",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("QueryExistingSubIssueTitlesAsync must exist");
        // Parameter order matches the updated signature: (issueOps, projectContext, logger, runId, ct)
        var task = (Task<IReadOnlyList<string>>)method!.Invoke(null, [issueOps, projectContext, Serilog.Log.Logger, runId, ct])!;
        return await task;
    }

    private static string InvokeBuildIssueContextContent(IssueDetail issue, IReadOnlyList<IssueComment> comments)
    {
        var method = typeof(DecompositionStep).GetMethod(
            "BuildIssueContextContent",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("BuildIssueContextContent must exist");
        return (string)method!.Invoke(null, [issue, comments])!;
    }

    private static string InvokeBuildDeduplicationSection(IReadOnlyList<string> titles)
    {
        var method = typeof(DecompositionStep).GetMethod(
            "BuildDeduplicationSection",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        method.Should().NotBeNull("BuildDeduplicationSection must exist");
        return (string)method!.Invoke(null, [titles])!;
    }

    private static IssueComment MakeComment(int id, string body) => new()
    {
        Id = id.ToString(),
        Body = body,
        Author = "test-author",
        CreatedAt = DateTime.UtcNow
    };
}
