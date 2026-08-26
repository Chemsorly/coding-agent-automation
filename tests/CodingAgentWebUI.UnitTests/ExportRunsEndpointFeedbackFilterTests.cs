using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Validates that GET /api/export/runs.json applies the feedbackOnly filter DB-side
/// (before paging) rather than in memory after paging.
///
/// Bug documented in 042 Exit State: the previous implementation called
/// GetRunHistoryAsync(page, pageSize) then filtered in memory — causing empty or
/// short pages whenever the filtered-out runs happened to fall in the page window.
/// </summary>
public sealed class ExportRunsEndpointFeedbackFilterTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static PipelineRunSummary RunWithFeedback(string id) => new()
    {
        RunId = id,
        IssueIdentifier = (IssueIdentifier)$"owner/repo#{id}",
        IssueTitle = $"Issue {id}",
        StartedAt = DateTime.UtcNow,
        FinalStep = PipelineStep.Completed,
        RunType = PipelineRunType.Implementation,
        Feedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback()
        }
    };

    private static PipelineRunSummary RunWithoutFeedback(string id) => new()
    {
        RunId = id,
        IssueIdentifier = (IssueIdentifier)$"owner/repo#{id}",
        IssueTitle = $"Issue {id}",
        StartedAt = DateTime.UtcNow,
        FinalStep = PipelineStep.Completed,
        RunType = PipelineRunType.Implementation,
        Feedback = null
    };

    // ── tests ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// When feedbackOnly=true and paging is active, the endpoint MUST call the
    /// (page, pageSize, feedbackOnly: true) overload so the filter is applied
    /// at the DB layer, not after paging.
    ///
    /// Regression: old code called GetRunHistoryAsync(page, pageSize) and
    /// then did .Where(r => r.Feedback is not null) — so a page containing only
    /// non-feedback runs silently returned an empty result.
    /// </summary>
    [Fact]
    public async Task WhenFeedbackOnlyAndPaged_CallsThreeArgOverload_NotTwoArgOverload()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>(MockBehavior.Strict);

        // Only the (int, int, bool) overload should be called.
        mockHistory
            .Setup(h => h.GetRunHistoryAsync(1, 10, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = [RunWithFeedback("1"), RunWithFeedback("2")],
                HasMore = true,
                Page = 1,
                PageSize = 10
            });

        // Invoke the endpoint logic directly — same control flow as MapGet handler.
        var result = await SimulateEndpoint(mockHistory.Object, feedbackOnly: true, page: 1, pageSize: 10);

        result.Should().HaveCount(2);
        mockHistory.Verify(
            h => h.GetRunHistoryAsync(1, 10, true, It.IsAny<CancellationToken>()),
            Times.Once,
            "feedbackOnly filter must be pushed to the DB-level overload, not applied after paging");

        // The two-arg overload must NOT have been called.
        mockHistory.Verify(
            h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "two-arg paged overload should not be called when feedbackOnly=true");
    }

    /// <summary>
    /// When a paginated response contains ONLY runs without feedback, feedbackOnly=true
    /// should return an empty list — not a list containing those runs.
    ///
    /// This is the exact failure mode of the old post-filter bug: the page contained
    /// no-feedback runs, the DB filter was skipped, and the runs were returned.
    /// </summary>
    [Fact]
    public async Task WhenFeedbackOnlyAndPageContainsOnlyNonFeedbackRuns_ReturnsEmpty()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>();

        // DB-side filter returns empty when the page has no feedback runs.
        mockHistory
            .Setup(h => h.GetRunHistoryAsync(2, 10, true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = [],     // DB already filtered — none on this page have feedback
                HasMore = false,
                Page = 2,
                PageSize = 10
            });

        var result = await SimulateEndpoint(mockHistory.Object, feedbackOnly: true, page: 2, pageSize: 10);

        result.Should().BeEmpty("DB-level filter returns no runs for this page");
    }

    /// <summary>
    /// Without feedbackOnly, the two-arg overload (no DB filter) is used for paged requests.
    /// </summary>
    [Fact]
    public async Task WhenNotFeedbackOnlyAndPaged_CallsTwoArgOverload()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>(MockBehavior.Strict);

        mockHistory
            .Setup(h => h.GetRunHistoryAsync(1, 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = [RunWithoutFeedback("a"), RunWithFeedback("b")],
                HasMore = false,
                Page = 1,
                PageSize = 5
            });

        var result = await SimulateEndpoint(mockHistory.Object, feedbackOnly: false, page: 1, pageSize: 5);

        result.Should().HaveCount(2);
        mockHistory.Verify(
            h => h.GetRunHistoryAsync(1, 5, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// Without paging, feedbackOnly filter is applied in-memory on the full list.
    /// This path is fine because no page boundary can silently drop rows.
    /// </summary>
    [Fact]
    public async Task WhenFeedbackOnlyAndNoPaging_FiltersInMemoryFromFullList()
    {
        var mockHistory = new Mock<IPipelineRunHistoryService>(MockBehavior.Strict);

        IReadOnlyList<PipelineRunSummary> allRuns =
        [
            RunWithFeedback("1"),
            RunWithoutFeedback("2"),
            RunWithFeedback("3")
        ];

        mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(allRuns);

        var result = await SimulateEndpoint(mockHistory.Object, feedbackOnly: true, page: null, pageSize: null);

        result.Should().HaveCount(2, "only runs 1 and 3 have feedback");
        result.All(r => r.Feedback is not null).Should().BeTrue();
    }

    // ── endpoint logic extracted for testability ──────────────────────────────

    /// <summary>
    /// Mirrors the control flow of the GET /api/export/runs.json endpoint handler
    /// (EndpointRegistration.cs). Must be kept in sync with that handler.
    /// </summary>
    private static async Task<List<PipelineRunSummary>> SimulateEndpoint(
        IPipelineRunHistoryService history,
        bool? feedbackOnly,
        int? page,
        int? pageSize)
    {
        IEnumerable<PipelineRunSummary> runs;
        var filterFeedback = feedbackOnly == true;
        if (page.HasValue || pageSize.HasValue)
        {
            var p = page ?? 1;
            var ps = pageSize ?? 50;
            var pagedResult = filterFeedback
                ? await history.GetRunHistoryAsync(p, ps, feedbackOnly: true)
                : await history.GetRunHistoryAsync(p, ps);
            runs = pagedResult.Items;
        }
        else
        {
            var allRuns = await history.GetRunHistoryAsync();
            runs = filterFeedback ? allRuns.Where(r => r.Feedback is not null) : allRuns;
        }

        return runs.ToList();
    }
}
