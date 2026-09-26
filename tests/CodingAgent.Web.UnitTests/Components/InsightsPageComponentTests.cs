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
        _history.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = runs.ToList(), Page = 1, PageSize = 500, HasMore = false });

    private static PipelineRunSummary Run(PipelineStep finalStep, IReadOnlyList<GateOutcome>? gates = null, long tokens = 0) => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "1",
        IssueTitle = "t",
        FinalStep = finalStep,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-5),
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
            1, 500, false, false, It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
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
        Returns(Run(PipelineStep.Completed), Run(PipelineStep.Failed));
        var cut = Render<Insights>();

        cut.Find("select[aria-label='Time window']").Change(windowValue);

        cut.FindAll(".cockpit-stat-l").Select(l => l.TextContent.Trim())
            .Should().Contain($"Success rate · {windowLabel}");
        // One bar column per day; the runs started minutes ago, so "All" spans just today.
        cut.FindAll("[title$='run(s)']").Should().HaveCount(expectedBuckets);
        // Month name is culture-dependent ("Sep", "Sept.", "Sep."), so only the shape is asserted.
        cut.FindAll("[title$='run(s)']").Last().GetAttribute("title").Should().MatchRegex(@"^\D+ \d{1,2} UTC — 2 run\(s\)$");
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
}
