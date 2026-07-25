using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

public class RecentRunsSectionTests : BunitContext
{
    [Fact]
    public void Renders_EmptyState_WhenNoHistory_AndExpanded()
    {
        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, Array.Empty<PipelineRunSummary>())
            .Add(s => s.IsExpanded, true));

        Assert.Contains("No completed runs yet.", cut.Markup);
    }

    [Fact]
    public void Renders_Table_WhenHistoryExists_AndExpanded()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Issue", PipelineStep.Completed)
        };

        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, history)
            .Add(s => s.IsExpanded, true));

        Assert.Contains("monitoring-table", cut.Markup);
        Assert.Contains("#42", cut.Markup);
    }

    [Fact]
    public void HidesTable_WhenNotExpanded()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Issue", PipelineStep.Completed)
        };

        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, history)
            .Add(s => s.IsExpanded, false));

        Assert.DoesNotContain("monitoring-table", cut.Markup);
    }

    [Fact]
    public void ExportLinks_AlwaysVisible()
    {
        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, Array.Empty<PipelineRunSummary>())
            .Add(s => s.IsExpanded, false));

        Assert.Contains("Export All (JSON)", cut.Markup);
        Assert.Contains("Feedback Only (JSON)", cut.Markup);
    }

    [Fact]
    public async Task Toggle_Emits_IsExpandedChanged()
    {
        bool? newValue = null;

        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, Array.Empty<PipelineRunSummary>())
            .Add(s => s.IsExpanded, true)
            .Add(s => s.IsExpandedChanged, EventCallback.Factory.Create<bool>(this, v => newValue = v)));

        await cut.InvokeAsync(() =>
        {
            cut.Find(".monitoring-section-toggle").Click();
        });

        Assert.Equal(false, newValue);
    }

    [Fact]
    public async Task RowClick_Emits_OnRunSelected()
    {
        PipelineRunSummary? selected = null;
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed)
        };

        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, history)
            .Add(s => s.IsExpanded, true)
            .Add(s => s.OnRunSelected, EventCallback.Factory.Create<PipelineRunSummary>(this, r => selected = r)));

        await cut.InvokeAsync(() =>
        {
            cut.Find("tr.monitoring-row-clickable").Click();
        });

        Assert.NotNull(selected);
        Assert.Equal("run-1", selected.RunId);
    }

    [Fact]
    public void RecentRunsTable_HasElevenColumns()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "1", "T", PipelineStep.Completed)
        };

        var cut = Render<RecentRunsSection>(p => p
            .Add(s => s.RunHistory, history)
            .Add(s => s.IsExpanded, true));

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(11, headerCells.Count);
    }

    private static PipelineRunSummary CreateSummary(
        string runId, string issueId, string issueTitle, PipelineStep finalStep)
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
            InitiatedBy = "manual"
        };
    }
}
