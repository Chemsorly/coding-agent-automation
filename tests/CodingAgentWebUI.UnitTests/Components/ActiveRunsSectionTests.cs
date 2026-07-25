using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

// TODO: Add test(s) exercising the consolidation run rendering path.
// Currently all tests pass Array.Empty<ConsolidationRun>() for ActiveConsolidationRuns,
// leaving ~25 lines of distinct template logic untested (consolidation badge, template name column,
// agent lookup via Agents.FirstOrDefault, "Running" status).
public class ActiveRunsSectionTests : BunitContext
{
    [Fact]
    public void Renders_EmptyState_WhenNoActiveRuns()
    {
        var cut = Render<ActiveRunsSection>(p => p
            .Add(s => s.ActiveRuns, Array.Empty<ActiveRunSummary>())
            .Add(s => s.ActiveConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.Agents, Array.Empty<AgentEntry>()));

        Assert.Contains("No active pipeline runs.", cut.Markup);
    }

    [Fact]
    public void Renders_ActiveRuns_InTable()
    {
        var runs = new List<ActiveRunSummary>
        {
            new()
            {
                RunId = "run-1",
                IssueIdentifier = "42",
                IssueTitle = "Test Issue",
                RunType = PipelineRunType.Implementation,
                AgentId = "agent-1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                ProjectName = null,
                CurrentStep = PipelineStep.GeneratingCode
            }
        };

        var cut = Render<ActiveRunsSection>(p => p
            .Add(s => s.ActiveRuns, runs)
            .Add(s => s.ActiveConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.Agents, Array.Empty<AgentEntry>()));

        Assert.Contains("monitoring-table", cut.Markup);
        Assert.Contains("#42", cut.Markup);
        Assert.Contains("Test Issue", cut.Markup);
    }

    [Fact]
    public async Task RowClick_Emits_OnRunSelected()
    {
        string? selectedRunId = null;
        var runs = new List<ActiveRunSummary>
        {
            new()
            {
                RunId = "run-abc",
                IssueIdentifier = "10",
                IssueTitle = "Click Test",
                RunType = PipelineRunType.Implementation,
                AgentId = "agent-1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                ProjectName = null,
                CurrentStep = PipelineStep.AnalyzingCode
            }
        };

        var cut = Render<ActiveRunsSection>(p => p
            .Add(s => s.ActiveRuns, runs)
            .Add(s => s.ActiveConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.Agents, Array.Empty<AgentEntry>())
            .Add(s => s.OnRunSelected, EventCallback.Factory.Create<string>(this, id => selectedRunId = id)));

        await cut.InvokeAsync(() =>
        {
            var row = cut.Find("tr.monitoring-row-clickable");
            row.Click();
        });

        Assert.Equal("run-abc", selectedRunId);
    }

    [Fact]
    public async Task CancelButton_Emits_OnCancelRun()
    {
        string? cancelledRunId = null;
        var runs = new List<ActiveRunSummary>
        {
            new()
            {
                RunId = "run-cancel",
                IssueIdentifier = "99",
                IssueTitle = "Cancel Test",
                RunType = PipelineRunType.Implementation,
                AgentId = "agent-1",
                StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                ProjectName = null,
                CurrentStep = PipelineStep.GeneratingCode
            }
        };

        var cut = Render<ActiveRunsSection>(p => p
            .Add(s => s.ActiveRuns, runs)
            .Add(s => s.ActiveConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.Agents, Array.Empty<AgentEntry>())
            .Add(s => s.OnCancelRun, EventCallback.Factory.Create<string>(this, id => cancelledRunId = id)));

        await cut.InvokeAsync(() =>
        {
            var btn = cut.FindAll("button.btn-cancel-small").First(b => b.TextContent.Contains("Cancel"));
            btn.Click();
        });

        Assert.Equal("run-cancel", cancelledRunId);
    }

    [Fact]
    public void ActiveRunsTable_HasNineColumns()
    {
        var runs = new List<ActiveRunSummary>
        {
            new()
            {
                RunId = "run-1",
                IssueIdentifier = "1",
                IssueTitle = "T",
                RunType = PipelineRunType.Implementation,
                AgentId = null,
                StartedAt = DateTimeOffset.UtcNow,
                ProjectName = null,
                CurrentStep = PipelineStep.AnalyzingCode
            }
        };

        var cut = Render<ActiveRunsSection>(p => p
            .Add(s => s.ActiveRuns, runs)
            .Add(s => s.ActiveConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.Agents, Array.Empty<AgentEntry>()));

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(9, headerCells.Count);
    }
}
