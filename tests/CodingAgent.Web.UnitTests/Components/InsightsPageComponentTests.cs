using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the Insights page. Covers the #2943 behaviour that a later merge had silently
/// reverted (page size, gate "no data" vs "no failures"), the outcome classification of terminal-like
/// steps, and the outcome-mix layout when the cost card is hidden.
/// </summary>
public class InsightsPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _history = new();

    public InsightsPageComponentTests()
    {
        Services.AddSingleton(_history.Object);
        Services.AddSingleton(new CockpitState());
        Services.AddSingleton(Mock.Of<IJSRuntime>());
    }

    private void Returns(params PipelineRunSummary[] runs) =>
        _history.Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = runs.ToList(), Page = 1, PageSize = 500, HasMore = false });

    private static PipelineRunSummary Run(PipelineStep finalStep, IReadOnlyList<GateOutcome>? gates = null, long tokens = 0, DateTimeOffset? startedAt = null) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "t",
        FinalStep = finalStep,
        StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
        CompletedAtOffset = DateTimeOffset.UtcNow,
        TotalTokens = tokens,
        QualityGateOutcomes = gates?.ToList() ?? [],
        InitiatedBy = "manual",
    };

    private static string? StatValue(IRenderedComponent<Insights> cut, string labelPrefix) =>
        cut.FindAll(".cockpit-stat")
            .FirstOrDefault(s => s.QuerySelector(".cockpit-stat-l")?.TextContent.Trim().StartsWith(labelPrefix) == true)
            ?.QuerySelector(".cockpit-stat-v")?.TextContent.Trim();

    [Fact]
    public void Load_RequestsFiveHundredRuns()
    {
        Returns(Run(PipelineStep.Completed));

        Render<Insights>();

        _history.Verify(c => c.GetRunHistoryAsync(
            It.Is<RunHistoryQuery>(q => q.Page == 1 && q.PageSize == 500 && !q.FeedbackOnly && !q.IncludeActive),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public void OutcomeMix_ClassifiesTerminalLikeSteps_AndSuccessRateIgnoresRestarts()
    {
        Returns(
            Run(PipelineStep.Completed),
            Run(PipelineStep.PrMerged),
            Run(PipelineStep.Failed),
            Run(PipelineStep.PrClosed),
            Run(PipelineStep.ConflictRestart));

        var cut = Render<Insights>();

        // succeeded 2 (completed + merged) of 4 decided (the restart doesn't count)
        StatValue(cut, "Success rate").Should().Be("50%");
        var legend = cut.Markup;
        legend.Should().Contain("Succeeded <strong").And.Contain("Restarted <strong");
        cut.FindAll("[title='Restarted: 1']").Should().ContainSingle("the restart gets its own outcome-mix segment");
        cut.FindAll("[title='Cancelled: 1']").Should().ContainSingle("a closed PR counts as cancelled");
        cut.FindAll("[title='Succeeded: 2']").Should().ContainSingle("a merged PR counts as succeeded");
    }

    [Fact]
    public void OutcomeMix_SpansFullWidth_WhenCostCardIsHidden()
    {
        Returns(Run(PipelineStep.Completed, tokens: 0));

        var cut = Render<Insights>();

        cut.FindAll("h2").Select(h => h.TextContent.Trim()).Should().NotContain("Cost");
        cut.FindAll(".cockpit-two-col-asymmetric").Should().BeEmpty(
            "without the cost card a two-column grid leaves an empty right-hand column");
    }

    [Fact]
    public void OutcomeMix_SharesTheRow_WhenCostCardIsShown()
    {
        Returns(Run(PipelineStep.Completed, tokens: 1200));

        var cut = Render<Insights>();

        cut.FindAll(".cockpit-two-col-asymmetric").Should().ContainSingle();
    }

    [Theory]
    [InlineData("168", "Last 7d", 7)]
    [InlineData("0", "All runs", 1)]
    public void TimeWindow_DailyWindows_UseDailyBucketsAndDateLabels(string windowValue, string windowLabel, int expectedBuckets)
    {
        // Pin StartedAtOffset to noon today UTC so the run is always in the current UTC day,
        // regardless of when the test runs (avoids midnight-boundary flakiness where -5 min
        // would place the run in the previous UTC day, producing 2 buckets for "All").
        var todayNoon = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero).AddHours(12);
        Returns(Run(PipelineStep.Completed, startedAt: todayNoon), Run(PipelineStep.Failed, startedAt: todayNoon));
        var cut = Render<Insights>();

        cut.Find("select[aria-label='Time window']").Change(windowValue);

        cut.FindAll(".cockpit-stat-l").Select(l => l.TextContent.Trim())
            .Should().Contain($"Success rate · {windowLabel}");
        // One bar column per day; the runs are pinned to today noon, so "All" spans just today.
        var columns = cut.FindAll("[title$='run(s)']");
        columns.Should().HaveCount(expectedBuckets);
        // Month name is culture-dependent ("Sep", "Sept.", "Sep."), so only the shape is asserted.
        columns[columns.Count - 1].GetAttribute("title").Should().MatchRegex(@"^\D+ \d{1,2} UTC — 2 run\(s\)$");
    }

    [Fact]
    public void GateCard_SaysNoGateData_WhenNoRunHasGateOutcomes()
    {
        Returns(Run(PipelineStep.Completed));

        var cut = Render<Insights>();

        cut.Markup.Should().Contain("No gate data for runs in this window.");
    }

    [Fact]
    public void GateCard_SaysNoFailures_WhenGatesRanAndAllPassed()
    {
        Returns(
            Run(PipelineStep.Completed, [new GateOutcome("Build", true)]),
            Run(PipelineStep.Completed, [new GateOutcome("Build", true)]));

        var cut = Render<Insights>();

        cut.Markup.Should().Contain("No gate failures in 2 runs with gate data.");
    }

    // ── Issue #3077 — server-side since filter ─────────────────────────────

    /// <summary>
    /// For hourly windows (24h), Insights must pass a non-null <c>since</c> value to the API client
    /// equal to the window start (approximately UtcNow minus the window hours, truncated to the hour).
    /// This verifies the pre-filter moved server-side rather than remaining a client-side LINQ filter.
    /// </summary>
    [Fact]
    public void Load_PassesWindowStartAsSinceToApiClient()
    {
        DateTimeOffset? capturedSince = DateTimeOffset.MaxValue; // sentinel — must be overwritten
        _history
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<RunHistoryQuery, CancellationToken>(
                (query, _) => capturedSince = query.Since)
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = [Run(PipelineStep.Completed)], Page = 1, PageSize = 500, HasMore = false });

        var cut = Render<Insights>();
        // Default window is 24h — a since value must be passed.
        // TODO: [WARNING] If the component's default window is already 24h, Render<Insights>() fires
        // GetRunHistoryAsync during initial render and overwrites capturedSince before Change("24") runs.
        // The assertions below then validate whichever call happened last (initial render or the
        // select change), not specifically the Change("24") call. If the default window changes to
        // something other than 24h, the Change fires the only non-null-since call and the test is
        // precise — but that's a coincidence. Fix: capture all invocations in a List and assert that
        // at least one had the expected since value, or initialise with the "All" window so only the
        // Change("24") triggers a non-null since. (TestQualityReviewer review finding #3077.)
        cut.Find("select[aria-label='Time window']").Change("24");

        capturedSince.Should().NotBeNull("the 24h window must pass a non-null since to the API");
        capturedSince.Should().NotBe(DateTimeOffset.MaxValue, "since must have been set by the callback");
        // The since value should be approximately UtcNow.AddHours(-24) truncated to the hour.
        var expectedSince = new DateTimeOffset(DateTimeOffset.UtcNow.UtcDateTime.Date, TimeSpan.Zero)
            .AddHours(DateTimeOffset.UtcNow.UtcDateTime.Hour)
            .AddHours(-24);
        capturedSince!.Value.Should().BeCloseTo(expectedSince, TimeSpan.FromMinutes(1),
            "since must be the window start (UtcNow truncated to hour minus 24h)");
    }

    /// <summary>
    /// For the "All" window (windowHours=0), Insights must pass <c>since = null</c> to the API client.
    /// Passing DateTimeOffset.MinValue would produce a spurious WHERE StartedAt >= '0001-01-01' clause.
    /// </summary>
    [Fact]
    public void Load_PassesNullSinceForAllWindow()
    {
        DateTimeOffset? capturedSince = DateTimeOffset.MaxValue; // sentinel
        _history
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .Callback<RunHistoryQuery, CancellationToken>(
                (query, _) => capturedSince = query.Since)
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = [Run(PipelineStep.Completed)], Page = 1, PageSize = 500, HasMore = false });

        var cut = Render<Insights>();
        cut.Find("select[aria-label='Time window']").Change("0"); // "All" window

        capturedSince.Should().BeNull("the 'All' window must pass null since — not DateTimeOffset.MinValue");
    }

    /// <summary>
    /// _isTruncated (the "results may be partial" subtitle note) must mirror <c>HasMore</c> exactly.
    /// When the server returns HasMore=true (there are more in-window runs), the note must appear.
    /// When HasMore=false, the note must not appear.
    /// </summary>
    [Fact]
    public void Load_IsTruncated_OnlyWhenHasMoreIsTrue()
    {
        // HasMore=true → "results may be partial" must be shown.
        _history
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = [Run(PipelineStep.Completed)],
                Page = 1,
                PageSize = 500,
                HasMore = true
            });

        var cut = Render<Insights>();

        cut.Find("[data-testid='insights-total']").ParentElement!.TextContent
            .Should().Contain("results may be partial",
                "when HasMore=true the subtitle must include the truncation note");

        // HasMore=false → no truncation note.
        _history
            .Setup(c => c.GetRunHistoryAsync(It.IsAny<RunHistoryQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = [Run(PipelineStep.Completed)],
                Page = 1,
                PageSize = 500,
                HasMore = false
            });

        cut.Find("select[aria-label='Time window']").Change("24"); // trigger a reload

        // TODO: [WARNING] The mock is re-configured (re-Setup above) and then Change("24") triggers
        // an async reload. In bUnit, async state updates may not be flushed synchronously before this
        // assertion runs. If the second GetRunHistoryAsync completes after the assertion, the component
        // may still show the HasMore=true markup from the first render, causing this assertion to pass
        // for the wrong reason (stale render). Use WaitForAssertion or WaitForState to make the
        // flush-dependency explicit and prevent the assertion from masking a real regression where
        // HasMore=false incorrectly keeps the truncation note visible.
        // (TestQualityReviewer review finding #3077.)
        cut.Find("[data-testid='insights-total']").ParentElement!.TextContent
            .Should().NotContain("results may be partial",
                "when HasMore=false the truncation note must not appear");
    }
}
