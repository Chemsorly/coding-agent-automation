using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests that cover the scenario where the HTTP completion report arrived but the
/// SignalR hub completion report was lost, leaving the issue stuck with <c>agent:in-progress</c>
/// even though the run actually succeeded.
///
/// <para>
/// Issue IDs used by this class: 7101–7103. These are reserved for this class to prevent
/// cross-test state leakage with other E2E tests.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class LostHubCompletionLabelTests : HeadlessE2ETestBase
{
    public LostHubCompletionLabelTests(E2EFixture fixture) : base(fixture) { }

    // ── Common arrange ────────────────────────────────────────────────────

    /// <summary>
    /// Seeds the template and issue required for each test, then inserts a Pending WorkItem
    /// and transitions it to Running.
    /// </summary>
    /// <returns>The <see cref="Guid"/> of the inserted WorkItem.</returns>
    private async Task<Guid> ArrangeIssueWithRunningWorkItemAsync(
        string issueId,
        string templateId,
        CancellationToken ct)
    {
        // Seed a template so OrphanedLabelRecoveryService finds "issue-e2e" to scan.
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = $"Lost-Hub Completion Template {templateId}",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, ct);

        // Add the issue with agent:in-progress (as it would be after dispatch).
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = $"Lost hub completion test issue {issueId}",
            Description = string.Empty,
            Labels = [AgentLabels.InProgress],
        });

        // Insert a Pending WorkItem and then set it to Running (simulating an active agent).
        var workItemId = await InsertPendingWorkItemAsync(issueId);
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
        item.Status = WorkItemStatus.Running;
        await db.SaveChangesAsync(ct);

        return workItemId;
    }

    // ── Tests ─────────────────────────────────────────────────────────────

    /// <summary>
    /// When the HTTP completion channel reports Succeeded but the hub channel never fires
    /// (e.g. hub connection was gone), the issue keeps <c>agent:in-progress</c>.
    /// Once the dedup cooldown has passed, the orphan sweep must apply <c>agent:done</c>
    /// — NOT <c>agent:error</c>.
    /// </summary>
    [Fact]
    public async Task WhenHttpSucceededAndHubReportNeverCame_SweepShouldLabelDoneNotError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        const string issueId = "7101";
        var workItemId = await ArrangeIssueWithRunningWorkItemAsync(
            issueId, "lost-hub-template-7101", ct);

        // ── Act 1: HTTP completion path (no hub) ──────────────────────────

        // The HTTP POST /api/work-items/{id}/status with Succeeded should return true (200).
        var postResult = await Fixture.WorkItems.PostStatusAsync(
            workItemId,
            new WorkItemStatusUpdate { Status = nameof(WorkItemStatus.Succeeded) },
            ct);
        Assert.True(postResult, "POST /api/work-items/{id}/status with Succeeded must return 200 (true)");

        // ── Assert 1: no label change from HTTP alone ─────────────────────

        var changesAfterHttp = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueId)
            .ToList();
        Assert.Empty(changesAfterHttp);

        // ── Act 2: backdate CompletedAt to push it outside the dedup cooldown, then sweep ──

        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
            item.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync(ct);
        }

        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // ── Assert 2: sweep must apply agent:done, not agent:error ─────────

        var adds = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueId && lc.Added)
            .Select(lc => lc.Label)
            .ToList();

        var removes = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueId && !lc.Added)
            .Select(lc => lc.Label)
            .ToList();

        Assert.Contains(AgentLabels.Done, adds);
        Assert.Contains(AgentLabels.InProgress, removes);
        Assert.DoesNotContain(AgentLabels.Error, adds);

        // Confirm the materialized issue state has exactly agent:done among its agent:* labels.
        var issue = Fixture.IssueProvider.Issues.First(i => i.Identifier == issueId);
        var agentLabels = issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([AgentLabels.Done], agentLabels);
    }

    /// <summary>
    /// When the Succeeded HTTP payload carries <c>FinalLabel = agent:next</c> (e.g. a
    /// ConflictRestart completion), the sweep must apply <c>agent:next</c> instead of
    /// <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenSucceededPayloadCarriesFinalLabel_SweepShouldApplyThatLabel()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        const string issueId = "7102";
        var workItemId = await ArrangeIssueWithRunningWorkItemAsync(
            issueId, "lost-hub-template-7102", ct);

        // Post Succeeded with a FinalLabel in the result payload.
        var result = JsonSerializer.Serialize(
            new JobCompletionPayload
            {
                FinalStep = PipelineStep.ConflictRestart,
                CompletedAt = DateTimeOffset.UtcNow,
                FinalLabel = AgentLabels.Next,
            },
            PipelineJsonOptions.Default);

        await Fixture.WorkItems.PostStatusAsync(
            workItemId,
            new WorkItemStatusUpdate { Status = nameof(WorkItemStatus.Succeeded), Result = result },
            ct);

        // Backdate CompletedAt to push past the dedup cooldown, then sweep.
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
            item.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync(ct);
        }

        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // Confirm the issue ends up with exactly agent:next among its agent:* labels.
        var issue = Fixture.IssueProvider.Issues.First(i => i.Identifier == issueId);
        var agentLabels = issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([AgentLabels.Next], agentLabels);
    }

    /// <summary>
    /// When the latest WorkItem ended as Failed (not Succeeded), the sweep must still apply
    /// <c>agent:error</c> as it did before this fix.
    /// </summary>
    [Fact]
    public async Task WhenLatestWorkItemFailed_SweepShouldStillLabelError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        const string issueId = "7103";
        var workItemId = await ArrangeIssueWithRunningWorkItemAsync(
            issueId, "lost-hub-template-7103", ct);

        // Set the row directly to Failed without going through the HTTP endpoint, so there is no
        // hub-triggered label swap and the issue keeps agent:in-progress.
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var item = await db.WorkItems.FirstAsync(w => w.Id == workItemId, ct);
            item.Status = WorkItemStatus.Failed;
            item.FailureReason = FailureReason.AgentError;
            item.CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
            await db.SaveChangesAsync(ct);
        }

        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // The issue must end up with exactly agent:error among its agent:* labels.
        var issue = Fixture.IssueProvider.Issues.First(i => i.Identifier == issueId);
        var agentLabels = issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Equal([AgentLabels.Error], agentLabels);
    }
}
