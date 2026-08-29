using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Components;

namespace CodingAgentWebUI.UnitTests.Components;

// TODO: Add test(s) covering consolidation run rows in the queue table.
// Currently all tests pass Array.Empty<ConsolidationRun>() for QueuedConsolidationRuns,
// leaving consolidation queue row rendering (~30 lines including badge, template name,
// QueuedRequiredLabels, and cancel button wiring to OnCancelConsolidation) completely untested.
public class JobQueueSectionTests : BunitContext
{
    [Fact]
    public void Renders_EmptyState_WhenNoQueuedJobs()
    {
        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, Array.Empty<PendingJob>())
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("No pending jobs in queue.", cut.Markup);
    }

    [Fact]
    public void Renders_QueuedJobs_InTable()
    {
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "55",
                IssueTitle = "Queued Issue",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                EnqueuedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
                InitiatedBy = "manual",
                RequiredLabels = Array.Empty<string>()
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("monitoring-table", cut.Markup);
        Assert.Contains("#55", cut.Markup);
        Assert.Contains("Queued Issue", cut.Markup);
    }

    [Fact]
    public async Task RemoveButton_Emits_OnRemoveFromQueue()
    {
        (string IssueIdentifier, string IssueProviderId)? removed = null;
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "77",
                IssueTitle = "Remove Me",
                IssueProviderId = "ip-test",
                RepoProviderId = "rp-1",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>()
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.OnRemoveFromQueue, EventCallback.Factory.Create<(string, string)>(this, args => removed = args)));

        await cut.InvokeAsync(() =>
        {
            var btn = cut.FindAll("button.btn-cancel-small").First(b => b.TextContent.Contains("Remove"));
            btn.Click();
        });

        Assert.NotNull(removed);
        Assert.Equal("77", removed.Value.IssueIdentifier);
        Assert.Equal("ip-test", removed.Value.IssueProviderId);
    }

    [Fact]
    public void JobQueueTable_HasNineColumns()
    {
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "1",
                IssueTitle = "T",
                IssueProviderId = "ip",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>()
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(9, headerCells.Count);
    }

    [Fact]
    public void Renders_ReviewBadge_ForReviewJob()
    {
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "PR-1",
                IssueTitle = "Review PR",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                RunType = PipelineRunType.Review,
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>()
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("run-type-review", cut.Markup);
        // TODO: This assertion is weaker than necessary — "Review" may appear elsewhere in the markup (column headers,
        // aria-labels, etc.), so it would pass even if the badge text were accidentally changed to "Review Job" or
        // "Reviewing". Prefer asserting the exact badge text scoped to the element carrying the run-type-review CSS
        // class, e.g. find the element with class run-type-review and assert its text content is exactly "Review".
        Assert.Contains("Review", cut.Markup);
    }

    [Fact]
    public void Renders_DecompBadge_ForDecompositionAnalysisJob()
    {
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "GH-50",
                IssueTitle = "Decomp Epic",
                IssueProviderId = "ip-1",
                RepoProviderId = "rp-1",
                RunType = PipelineRunType.DecompositionAnalysis,
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>()
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("run-type-decomp", cut.Markup);
        Assert.Contains("Decomp (A)", cut.Markup);
    }
}
