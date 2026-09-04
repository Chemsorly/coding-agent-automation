using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Insights page — time-window dropdown, filtered stats, subtitle.
/// </summary>
public class InsightsPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _mockHistory = new();
    private readonly CockpitState _state = new();

    public InsightsPageComponentTests()
    {
        Services.AddSingleton(_mockHistory.Object);
        Services.AddSingleton(_state);
    }

    private static PagedResult<PipelineRunSummary> RunsPage(IReadOnlyList<PipelineRunSummary> items) => new()
    {
        Items = items,
        Page = 1,
        PageSize = 100,
        HasMore = false
    };

    [Fact]
    public void DefaultTimeWindowIs24h_ShouldShowDropdownWithCorrectDefault()
    {
        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(Array.Empty<PipelineRunSummary>()));

        var cut = Render<Insights>();

        // The dropdown should exist in the header
        var select = cut.Find("select");
        Assert.NotNull(select);

        // Default window is 24h
        // TODO: This only asserts the state object's own default — it does not verify that the component
        // renders the correct selected option or displays "(24h)" in the markup. Add assertions like
        // Assert.Contains("24h", cut.Markup) and verify the select element's value attribute equals "24".
        Assert.Equal("24", _state.RecentWindowHours.ToString());
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo1h_ShouldUpdateStatLabels()
    {
        var now = DateTimeOffset.UtcNow;
        // Provide a run within the 1h window so the stat strip is rendered.
        var runs = new[]
        {
            new PipelineRunSummary
            {
                RunId = "r1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = now.AddMinutes(-30),
                CompletedAtOffset = now.AddMinutes(-20)
            }
        };

        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(runs));

        var cut = Render<Insights>();

        // Wait for initial load to complete (stat strip visible).
        cut.WaitForAssertion(() => Assert.Contains("cockpit-stat-strip", cut.Markup), timeout: TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("1");
        });

        // Wait for re-load triggered by the window change.
        cut.WaitForAssertion(() => Assert.Contains("Success rate (1h)", cut.Markup), timeout: TimeSpan.FromSeconds(3));
        Assert.Contains("Avg cycle time (1h)", cut.Markup);
        Assert.Contains("Retry rate (1h)", cut.Markup);
        Assert.Contains("Avg tokens / run (1h)", cut.Markup);
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo1h_ShouldUpdateSubtitle()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new[]
        {
            // Within 1h
            new PipelineRunSummary
            {
                RunId = "r1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = now.AddMinutes(-30),
                CompletedAtOffset = now.AddMinutes(-20)
            },
            // Outside 1h (2h ago) — should be excluded
            new PipelineRunSummary
            {
                RunId = "r2", IssueIdentifier = "2", IssueTitle = "B",
                FinalStep = PipelineStep.Failed,
                StartedAtOffset = now.AddHours(-2),
                CompletedAtOffset = now.AddHours(-1).AddMinutes(-50)
            },
        };

        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(runs));

        var cut = Render<Insights>();

        // Wait for initial load
        cut.WaitForAssertion(() => Assert.Contains("cockpit-stat-strip", cut.Markup), timeout: TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("1");
        });

        // Subtitle should reflect the windowed count (1 run within 1h) and the label
        cut.WaitForAssertion(() => Assert.Contains("in the last 1h", cut.Markup), timeout: TimeSpan.FromSeconds(3));
        Assert.Contains("1 run", cut.Markup);
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo1h_ShouldFilterRunsToLastHour()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new[]
        {
            // Within 1h — completed
            new PipelineRunSummary
            {
                RunId = "r1", IssueIdentifier = "1", IssueTitle = "A",
                FinalStep = PipelineStep.Completed,
                StartedAtOffset = now.AddMinutes(-30),
                CompletedAtOffset = now.AddMinutes(-20)
            },
            // Outside 1h — failed
            new PipelineRunSummary
            {
                RunId = "r2", IssueIdentifier = "2", IssueTitle = "B",
                FinalStep = PipelineStep.Failed,
                StartedAtOffset = now.AddHours(-2),
                CompletedAtOffset = now.AddHours(-1).AddMinutes(-50)
            },
        };

        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(runs));

        var cut = Render<Insights>();

        // Wait for initial load
        cut.WaitForAssertion(() => Assert.Contains("cockpit-stat-strip", cut.Markup), timeout: TimeSpan.FromSeconds(3));

        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("1");
        });

        // Only 1 run in the 1h window, and it's completed: success rate should be 100%
        // TODO: Assert.Contains("100", ...) is overly broad — "100" may appear in other markup (PageSize,
        // etc.) and would pass even if the success rate computation were broken. Tighten to assert
        // "100%" within the specific success rate stat element, e.g. Assert.Contains("100%", cut.Markup).
        cut.WaitForAssertion(() => Assert.Contains("100", cut.Markup), timeout: TimeSpan.FromSeconds(3));
    }
}
