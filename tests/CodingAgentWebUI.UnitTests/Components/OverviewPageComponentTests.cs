using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Overview page — time-window dropdown, "recent" metric labels.
/// </summary>
public class OverviewPageComponentTests : BunitContext
{
    private readonly Mock<ILoopStatusService> _mockLoop = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockHistory = new();
    private readonly Mock<IPipelineApiAgentClient> _mockAgents = new();
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItems = new();
    private readonly CockpitState _state = new();

    public OverviewPageComponentTests()
    {
        _mockLoop.SetupGet(l => l.IsLoopActive).Returns(false);
        _mockLoop.SetupGet(l => l.IsSchedulerUnreachable).Returns(false);
        _mockLoop.SetupGet(l => l.IsCircuitBroken).Returns(false);
        _mockLoop.SetupGet(l => l.ProcessedCount).Returns(0);
        _mockLoop.SetupGet(l => l.FailedCount).Returns(0);
        _mockLoop.SetupGet(l => l.CurrentCycleTemplateIndex).Returns(0);
        _mockLoop.SetupGet(l => l.CurrentCycleTemplateCount).Returns(1);

        _mockAgents
            .Setup(a => a.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentEntry>());

        _mockWorkItems
            .Setup(w => w.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PendingWorkItemDto>());

        Services.AddSingleton(_mockLoop.Object);
        Services.AddSingleton(_mockHistory.Object);
        Services.AddSingleton(_mockAgents.Object);
        Services.AddSingleton(_mockWorkItems.Object);
        Services.AddSingleton(_state);
    }

    private static PagedResult<PipelineRunSummary> RunsPage(IReadOnlyList<PipelineRunSummary> items) => new()
    {
        Items = items,
        Page = 1,
        PageSize = 50,
        HasMore = false
    };

    [Fact]
    public void WhenTimeWindowIs24h_ShouldShowLabelWith24h()
    {
        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(Array.Empty<PipelineRunSummary>()));

        var cut = Render<Overview>();

        // Default window is 24h — stat labels should reflect this.
        // TODO: This test passes purely because the label text is hard-coded in Razor markup at render time
        // (before any async fetch). It does not exercise the time-window interaction path at all and would
        // pass even if SetRecentWindowHours/OnRecentWindowChanged were removed. Consider adding a test that
        // changes the window and then waits for the label to update to actually verify the mechanism.
        Assert.Contains("Success (24h)", cut.Markup);
        Assert.Contains("Tokens (24h)", cut.Markup);
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo1h_ShouldShowLabelWith1h()
    {
        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(Array.Empty<PipelineRunSummary>()));

        var cut = Render<Overview>();

        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("1");
        });

        cut.WaitForAssertion(() => Assert.Contains("Success (1h)", cut.Markup), timeout: TimeSpan.FromSeconds(3));
        Assert.Contains("Tokens (1h)", cut.Markup);
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo7d_ShouldShowLabelWith7d()
    {
        _mockHistory
            .Setup(h => h.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(RunsPage(Array.Empty<PipelineRunSummary>()));

        var cut = Render<Overview>();

        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("168");
        });

        cut.WaitForAssertion(() => Assert.Contains("Success (7d)", cut.Markup), timeout: TimeSpan.FromSeconds(3));
        Assert.Contains("Tokens (7d)", cut.Markup);
    }

    [Fact]
    public async Task WhenTimeWindowChangedTo1h_ShouldRescope_SuccessRate()
    {
        // Run from 2h ago should be excluded from 1h window.
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
            // Outside 1h — completed: should be excluded when window is 1h
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

        var cut = Render<Overview>();

        // Switch to 1h window
        await cut.InvokeAsync(() =>
        {
            var select = cut.Find("select");
            select.Change("1");
        });

        // Only r1 (completed) is within 1h. Success rate should be 100%.
        cut.WaitForAssertion(() => Assert.Contains("100%", cut.Markup), timeout: TimeSpan.FromSeconds(3));
    }
}
