using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

// TODO: Add tests covering Review-specific conditional rendering (ReviewPrUrl, CodeReviewAgentsRun list,
// findings counts with critical/warning/suggestion breakdown, and "No issues found" fallback).
// Also add tests for Decomposition-specific rendering (DecompositionSubIssuesCreated/DecompositionSubIssuesAttempted).
// Regressions in these paths (e.g., null reference on CodeReviewAgentsRun) would not be caught currently.
public class HistoryRunDetailModalTests : BunitContext
{
    [Fact]
    public void DoesNotRender_WhenNotVisible()
    {
        var run = CreateSummary("run-1", "42", "Test", PipelineStep.Completed);

        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.IsVisible, false));

        Assert.DoesNotContain("modal-overlay", cut.Markup);
    }

    [Fact]
    public void Renders_WhenVisible()
    {
        var run = CreateSummary("run-1", "42", "Test Issue", PipelineStep.Completed);

        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.IsVisible, true));

        Assert.Contains("modal-overlay", cut.Markup);
        Assert.Contains("#42", cut.Markup);
    }

    [Fact]
    public void ShowsFailureCallout_ForFailedRun()
    {
        var run = CreateSummary("run-1", "42", "Test", PipelineStep.Failed,
            failureReason: "Analysis failed after 2 attempts");

        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.IsVisible, true));

        var callout = cut.Find(".summary-failure-callout");
        Assert.Contains("Analysis failed after 2 attempts", callout.TextContent);
    }

    [Fact]
    public void NoFailureCallout_ForCompletedRun()
    {
        var run = CreateSummary("run-1", "42", "Test", PipelineStep.Completed);

        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.IsVisible, true));

        Assert.Empty(cut.FindAll(".summary-failure-callout"));
    }

    [Fact]
    public async Task CloseButton_Emits_OnDismiss()
    {
        bool dismissed = false;
        var run = CreateSummary("run-1", "42", "Test", PipelineStep.Completed);

        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, run)
            .Add(s => s.IsVisible, true)
            .Add(s => s.OnDismiss, EventCallback.Factory.Create(this, () => dismissed = true)));

        await cut.InvokeAsync(() =>
        {
            cut.Find(".modal-card .btn-cancel").Click();
        });

        Assert.True(dismissed);
    }

    [Fact]
    public void DoesNotRender_WhenRunIsNull()
    {
        var cut = Render<HistoryRunDetailModal>(p => p
            .Add(s => s.Run, (PipelineRunSummary?)null)
            .Add(s => s.IsVisible, true));

        Assert.DoesNotContain("modal-overlay", cut.Markup);
    }

    private static PipelineRunSummary CreateSummary(
        string runId, string issueId, string issueTitle, PipelineStep finalStep,
        string? failureReason = null)
    {
        var start = DateTime.UtcNow.AddMinutes(-30);
        return new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = issueId,
            IssueTitle = issueTitle,
            FinalStep = finalStep,
            StartedAt = start,
            CompletedAt = start.AddMinutes(15),
            InitiatedBy = "manual",
            FailureReason = failureReason
        };
    }
}
