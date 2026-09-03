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
    public void JobQueueTable_HasTenColumns()
    {
        // Updated from 9 → 10 columns after adding the Priority column (#2173).
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
        Assert.Equal(10, headerCells.Count);
    }

    [Fact]
    public void JobQueueSection_RendersPriority_ForQueuedJob()
    {
        // #2173: Priority column must render with correct value and range constraints.
        var workItemId = Guid.NewGuid();
        var jobs = new List<PendingJob>
        {
            new()
            {
                WorkItemId = workItemId.ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Priority Job",
                IssueProviderId = "github",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>(),
                PriorityWeight = 300
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        var input = cut.Find("input[type=number]");
        Assert.Equal("300", input.GetAttribute("value"));
        Assert.Equal("0", input.GetAttribute("min"));
        Assert.Equal("1000", input.GetAttribute("max"));
        // TODO: Add assertions that the input is enabled (disabled attribute absent) when WorkItemId is a
        // valid GUID, and disabled when WorkItemId is empty/non-GUID, to cover the conditional rendering path.
    }

    [Fact]
    public async Task JobQueueSection_OnSetPriority_FiresCallback()
    {
        // #2173: Changing the priority input must fire OnSetPriority with the correct (Id, Weight) tuple.
        var workItemId = Guid.NewGuid();
        (Guid Id, int Weight)? received = null;
        var jobs = new List<PendingJob>
        {
            new()
            {
                WorkItemId = workItemId.ToString(),
                IssueIdentifier = "10",
                IssueTitle = "Priority Test",
                IssueProviderId = "github",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>(),
                PriorityWeight = 0
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>())
            .Add(s => s.OnSetPriority, EventCallback.Factory.Create<(Guid, int)>(this, args => received = args)));

        await cut.InvokeAsync(async () =>
        {
            var input = cut.Find("input[type=number]");
            await input.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = "750" });
        });

        Assert.NotNull(received);
        Assert.Equal(workItemId, received.Value.Id);
        Assert.Equal(750, received.Value.Weight);
        // TODO: Add test cases to verify OnSetPriority is NOT fired for out-of-range values (e.g. 1001, -1)
        // to cover the client-side validation gate in the @onchange handler. A future removal of the range
        // guard would not be caught by existing tests.
        // TODO: Add test case to verify OnSetPriority is NOT fired when WorkItemId is absent or an invalid
        // GUID (covers the `if (!hasWorkItemId) return;` guard in the @onchange handler).
    }

    [Fact]
    public void JobQueueSection_ConsolidationRows_DoNotHavePriorityInput()
    {
        // #2173: Consolidation rows must not render a priority input — only QueuedJobs rows do.
        var jobs = new List<PendingJob>
        {
            new()
            {
                WorkItemId = Guid.NewGuid().ToString(),
                IssueIdentifier = "1",
                IssueTitle = "Impl Job",
                IssueProviderId = "github",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>()
            }
        };
        var consolidationRuns = new List<ConsolidationRun>
        {
            new()
            {
                RunId = Guid.NewGuid().ToString(),
                Type = ConsolidationRunType.BrainConsolidation,
                Status = ConsolidationRunStatus.Queued,
                StartedAtUtc = DateTimeOffset.UtcNow
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, consolidationRuns));

        // One input from the job row; the consolidation row must not contribute any
        var inputs = cut.FindAll("input[type=number]");
        Assert.Single(inputs);
    }

    [Fact]
    public void JobQueueSection_ConsolidationRows_HaveTenCells()
    {
        // #2173: Consolidation rows must include an empty <td> for the Priority column to
        // keep all 10 columns aligned. A missing <td> would visually shift the Actions column.
        var consolidationRuns = new List<ConsolidationRun>
        {
            new()
            {
                RunId = Guid.NewGuid().ToString(),
                Type = ConsolidationRunType.BrainConsolidation,
                Status = ConsolidationRunStatus.Queued,
                StartedAtUtc = DateTimeOffset.UtcNow
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, Array.Empty<PendingJob>())
            .Add(s => s.QueuedConsolidationRuns, consolidationRuns));

        var rows = cut.FindAll(".monitoring-table tbody tr");
        Assert.Single(rows);
        var cells = rows[0].QuerySelectorAll("td");
        Assert.Equal(10, cells.Length);
    }

    [Fact]
    public void Renders_ReviewBadge_ForReviewRunType()
    {
        // Regression guard for #2159: queued Review jobs must render the "run-type-review" badge,
        // not the default "run-type-impl" badge.
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "PR-42",
                IssueTitle = "My PR",
                IssueProviderId = "github",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>(),
                RunType = PipelineRunType.Review
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("run-type-review", cut.Markup);
        Assert.DoesNotContain("run-type-impl", cut.Markup);
    }

    [Fact]
    public void Renders_DecompBadge_ForDecompositionAnalysisRunType()
    {
        // Regression guard for #2159: pending Decomposition jobs are always Phase 1 (analysis)
        // and must render the "run-type-decomp" badge with "Decomp (A)" label, not "Impl".
        var jobs = new List<PendingJob>
        {
            new()
            {
                IssueIdentifier = "GH-100",
                IssueTitle = "Big Feature",
                IssueProviderId = "github",
                RepoProviderId = "rp",
                EnqueuedAt = DateTimeOffset.UtcNow,
                InitiatedBy = "test",
                RequiredLabels = Array.Empty<string>(),
                RunType = PipelineRunType.DecompositionAnalysis
            }
        };

        var cut = Render<JobQueueSection>(p => p
            .Add(s => s.QueuedJobs, jobs)
            .Add(s => s.QueuedConsolidationRuns, Array.Empty<ConsolidationRun>()));

        Assert.Contains("run-type-decomp", cut.Markup);
        Assert.Contains("Decomp (A)", cut.Markup);
        Assert.DoesNotContain("run-type-impl", cut.Markup);
    }
}
