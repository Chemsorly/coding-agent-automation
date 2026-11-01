using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for the three <see cref="CodingAgent.JobController.Reconciliation.ReconciliationLoop"/>
/// methods that previously had no E2E coverage:
/// <c>EnforceTimeoutsAsync</c>, <c>EnforceDispatchedTimeoutAsync</c>, and
/// <c>OnLeadershipAcquired</c>.
///
/// <para>
/// Tests drive the real reconciliation loop directly (not through the polling service) against
/// the in-process API host. All items use the <c>"jc-e2e"</c> agent selector, which no
/// <see cref="CodingAgent.Web.E2ETests.Infrastructure.FakeAgentClient"/> is registered for,
/// so the <see cref="CodingAgent.Web.E2ETests.Infrastructure.FakeJobController"/> poll loop
/// ignores them.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "JobController")]
[Collection(E2ECollection.Name)]
public sealed class ReconciliationEnforcementTests : HeadlessE2ETestBase
{
    public ReconciliationEnforcementTests(E2EFixture fixture) : base(fixture) { }

    // ── Seed helper ───────────────────────────────────────────────────────

    /// <summary>
    /// Adds issue <paramref name="issueIdentifier"/> with labels ["agent:in-progress"];
    /// InsertPendingWorkItemAsync(<paramref name="issueIdentifier"/>, agentSelector: "jc-e2e",
    /// <paramref name="timeoutSeconds"/>); transitions to Dispatched with
    /// K8sJobName = JobNameFactory.ForBrain(id); when status is Running transitions on to Running;
    /// then sets DispatchedAt = UtcNow - <paramref name="age"/> through a tracked entity in a new
    /// DbContext; adds the run to Fixture.RunService; when <paramref name="withJob"/> adds a V1Job
    /// to CreatedJobs with labels app.kubernetes.io/managed-by=caa-orchestrator,
    /// app.kubernetes.io/component=agent-job, caa/work-item-id=id, CreationTimestamp = UtcNow.
    /// </summary>
    private async Task<(Guid Id, string JobName)> SeedActiveItemAsync(
        string issueIdentifier,
        WorkItemStatus status,
        TimeSpan age,
        int timeoutSeconds = 3600,
        bool withJob = true)
    {
        // 1. Seed the issue so FailRunWithLabelAsync can apply the terminal label.
        Fixture.IssueProvider.Issues.Add(new CodingAgent.Pipeline.Models.IssueDetail
        {
            Identifier = issueIdentifier,
            Title = "Test issue",
            Description = "## Requirements\nTest\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = [AgentLabels.InProgress]
        });

        // 2. Insert work item into DB directly (bypasses orchestration).
        var id = await InsertPendingWorkItemAsync(
            issueIdentifier, agentSelector: "jc-e2e", timeoutSeconds: timeoutSeconds);
        var jobName = JobNameFactory.ForBrain(id);

        // 3. Transition to Dispatched, recording K8sJobName.
        var ts = Fixture.ApiServices.GetRequiredService<WorkItemTransitionService>();
        var dispatched = await ts.TransitionAsync(
            id, WorkItemStatus.Dispatched,
            w => { w.DispatchedAt = DateTimeOffset.UtcNow; w.K8sJobName = jobName; });
        if (!dispatched)
            throw new InvalidOperationException($"Failed to transition {id} to Dispatched");

        // 4. Optionally transition to Running.
        if (status == WorkItemStatus.Running)
        {
            var running = await ts.TransitionAsync(id, WorkItemStatus.Running);
            if (!running)
                throw new InvalidOperationException($"Failed to transition {id} to Running");
        }

        // 5. Register a PipelineRun so FailRunWithLabelAsync has a run to act on.
        //    Without this, the lifecycle manager returns without calling AddLabelsAsync, and
        //    the AgentLabels.Error label is never written to IssueProvider.LabelChanges.
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = id.ToString(),
            IssueIdentifier = new IssueIdentifier(issueIdentifier),
            IssueTitle = "Test issue",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "jc-e2e-test"
        });
        Fixture.RunService.AddRun(run);

        // 6. Backdate DispatchedAt AFTER all transitions.
        //    Each transition may stamp its own timestamps; the backdate must be the last write.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var row = await db.WorkItems.FindAsync(id);
        row!.DispatchedAt = DateTimeOffset.UtcNow - age;
        await db.SaveChangesAsync();

        // 7. Add a fake K8s Job when requested.
        //    The job carries the three labels required by H07's ListJobsAsync filter and by
        //    ReconciliationLoop.ParseWorkItemId.
        if (withJob)
        {
            Fixture.K8sClient.CreatedJobs[jobName] = new V1Job
            {
                Metadata = new V1ObjectMeta
                {
                    Name = jobName,
                    Labels = new Dictionary<string, string>
                    {
                        ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                        ["app.kubernetes.io/component"]  = "agent-job",
                        ["caa/work-item-id"]             = id.ToString()
                    },
                    CreationTimestamp = DateTime.UtcNow
                },
                Status = new V1JobStatus()
            };
        }

        return (id, jobName);
    }

    // ── Timeout enforcement (EnforceTimeoutsAsync) ────────────────────────

    [Fact]
    public async Task WhenARunningItemIsPastItsTimeout_ShouldFailWithTimeout_AndDeleteTheJob()
    {
        // Arrange: Running item, age 200 s, timeout 120 s → 200 > 120 and 200 > 60-s floor.
        var (id, jobName) = await SeedActiveItemAsync(
            "jc-timeout-1", WorkItemStatus.Running, TimeSpan.FromSeconds(200), timeoutSeconds: 120);

        // Act.
        await Fixture.RealReconciliationLoop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: DB row is Failed with Timeout reason and correct message.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.Timeout, item.FailureReason);
        Assert.Equal("Agent timeout after 120s", item.ErrorMessage);
        Assert.NotNull(item.CompletedAt);

        // Assert: K8s Job was deleted.
        Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);
        Assert.DoesNotContain(jobName, Fixture.K8sClient.CreatedJobs.Keys);

        // Assert: issue received the terminal label.
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges.Any(
                lc => lc.Identifier == "jc-timeout-1" && lc.Label == AgentLabels.Error && lc.Added),
            timeout: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenARunningItemIsWithinItsTimeout_ShouldNotEnforce()
    {
        // Arrange: Running item, age 200 s, timeout 3600 s → 200 < 3600, not timed out.
        var (id, jobName) = await SeedActiveItemAsync(
            "jc-timeout-2", WorkItemStatus.Running, TimeSpan.FromSeconds(200), timeoutSeconds: 3600);

        // Act.
        await Fixture.RealReconciliationLoop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: item is still Running; Job is still alive.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Running, item!.Status);
        Assert.DoesNotContain(jobName, Fixture.K8sClient.DeletedJobs);
    }

    [Fact]
    public async Task WhenTheAgeIsBelowTheSixtySecondFloor_ShouldNotEnforce()
    {
        // Arrange: Running item, age 40 s, timeout 30 s.
        //   Even though 40 > 30, GetActiveAsync(60) does not return items younger than 60 s,
        //   so the 60-second canary floor prevents enforcement.
        var (id, _) = await SeedActiveItemAsync(
            "jc-timeout-3", WorkItemStatus.Running, TimeSpan.FromSeconds(40), timeoutSeconds: 30);

        // Act.
        await Fixture.RealReconciliationLoop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: item is still Running.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Running, item!.Status);
    }

    [Fact]
    public async Task WhenADispatchedItemIsPastItsTimeout_ShouldLeaveItToTheDispatchedSweep()
    {
        // Arrange: Dispatched item, age 200 s, timeout 120 s.
        //   EnforceTimeoutsAsync only processes Running items.
        var (id, _) = await SeedActiveItemAsync(
            "jc-timeout-4", WorkItemStatus.Dispatched, TimeSpan.FromSeconds(200), timeoutSeconds: 120);

        // Act.
        await Fixture.RealReconciliationLoop.EnforceTimeoutsAsync(CancellationToken.None);

        // Assert: item is still Dispatched; the dispatched-timeout sweep owns it.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Dispatched, item!.Status);
    }

    // ── Dispatched-timeout enforcement (EnforceDispatchedTimeoutAsync) ────

    [Fact]
    public async Task WhenADispatchedItemHasNoJob_ShouldFailWithTimeout()
    {
        // Arrange: Dispatched item with no K8s Job, 1 hour old (well past ChatPodConnectTimeoutSeconds=30).
        var (id, _) = await SeedActiveItemAsync(
            "jc-nojob-1", WorkItemStatus.Dispatched, TimeSpan.FromHours(1), withJob: false);

        // Act: fetch the current job list (empty — no job was added) and run the sweep.
        var jobs = await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None);
        await Fixture.RealReconciliationLoop.EnforceDispatchedTimeoutAsync(jobs, CancellationToken.None);

        // Assert: DB row is Failed with Timeout reason; message mentions the elapsed seconds.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.Timeout, item.FailureReason);
        // The exact number depends on ChatPodConnectTimeoutSeconds config; do not hardcode it.
        Assert.StartsWith("No live K8s Job found ", item.ErrorMessage);
        Assert.EndsWith("s after dispatch", item.ErrorMessage);

        // Assert: issue received the terminal label.
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges.Any(
                lc => lc.Identifier == "jc-nojob-1" && lc.Label == AgentLabels.Error && lc.Added),
            timeout: TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task WhenADispatchedItemHasALiveJob_ShouldKeepIt()
    {
        // Arrange: Dispatched item with a live K8s Job, 1 hour old.
        var (id, _) = await SeedActiveItemAsync(
            "jc-nojob-2", WorkItemStatus.Dispatched, TimeSpan.FromHours(1), withJob: true);

        // Act: fetch the job list — the job IS in CreatedJobs and is returned.
        var jobs = await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None);
        await Fixture.RealReconciliationLoop.EnforceDispatchedTimeoutAsync(jobs, CancellationToken.None);

        // Assert: item is still Dispatched (live job → skip).
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Dispatched, item!.Status);
    }

    [Fact]
    public async Task WhenARunningItemHasNoJob_ShouldNotBeTouchedByTheDispatchedSweep()
    {
        // Arrange: Running item with no K8s Job, 1 hour old.
        //   EnforceDispatchedTimeoutAsync only processes Dispatched items.
        var (id, _) = await SeedActiveItemAsync(
            "jc-nojob-3", WorkItemStatus.Running, TimeSpan.FromHours(1), withJob: false);

        // Act.
        var jobs = await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None);
        await Fixture.RealReconciliationLoop.EnforceDispatchedTimeoutAsync(jobs, CancellationToken.None);

        // Assert: item is still Running (the dispatched sweep ignores non-Dispatched items).
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Running, item!.Status);
    }

    // ── Leadership cache reset (OnLeadershipAcquired + ReconcileOnceAsync) ─

    [Fact]
    public async Task WhenAJobWasReconciled_ShouldSkipIt_UntilLeadershipIsAcquired()
    {
        // Arrange: Running item, age 200 s.
        var (id, jobName) = await SeedActiveItemAsync(
            "jc-leader-1", WorkItemStatus.Running, TimeSpan.FromSeconds(200));

        // Simulate the K8s Job failing so ReconcileOnceAsync detects it.
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName);

        // Act (first reconciliation): item is reconciled to Failed; id is cached.
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // Assert: item is now Failed.
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            Assert.NotNull(item);
            Assert.Equal(WorkItemStatus.Failed, item!.Status);
        }

        // Reset the row back to Running through a tracked entity to simulate a stale job
        // that the new leader must re-reconcile.
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var row = await db.WorkItems.FindAsync(id);
            row!.Status       = WorkItemStatus.Running;
            row.CompletedAt   = null;
            row.ErrorMessage  = null;
            row.FailureReason = null;
            await db.SaveChangesAsync();
        }

        // Act (second reconciliation): id is still in _reconciledTerminalIds → skipped.
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // Assert: item is still Running (cache hit → no PostStatusAsync call).
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            Assert.NotNull(item);
            Assert.Equal(WorkItemStatus.Running, item!.Status);
        }

        // Re-add the job to CreatedJobs before the third reconciliation.
        // FailRunCoreAsync.TryDeleteJobForRunAsync deletes the K8s job when the run is found
        // (during the first reconciliation's DispatchTransitionedAsync → FailRunCoreAsync path).
        // In production, the K8s retention window keeps the job visible to a new leader for
        // 600 s; re-inserting it here reproduces that scenario for the test.
        Fixture.K8sClient.CreatedJobs[jobName] = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"]  = "agent-job",
                    ["caa/work-item-id"]             = id.ToString()
                },
                CreationTimestamp = DateTime.UtcNow
            },
            Status = new V1JobStatus()
        };
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName);

        // Act: acquire leadership (clears _reconciledTerminalIds), then reconcile again.
        Fixture.RealReconciliationLoop.OnLeadershipAcquired();
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // Assert: item is Failed again — the cache was cleared so the job was re-processed.
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            Assert.NotNull(item);
            Assert.Equal(WorkItemStatus.Failed, item!.Status);
        }
    }

    [Fact]
    public async Task WhenLeadershipIsAcquiredAfterATerminalPost_ShouldRepostIdempotently()
    {
        // Arrange: Running item, age 200 s.
        var (id, jobName) = await SeedActiveItemAsync(
            "jc-leader-2", WorkItemStatus.Running, TimeSpan.FromSeconds(200));

        // Simulate the K8s Job failing.
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName);

        // Act (first reconciliation): item transitions to Failed; label swap fires.
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // Wait for the terminal label to land (the full PostStatus→FailRunWithLabelAsync→
        // AddLabelsAsync chain is awaited in-process, so this returns immediately).
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges.Any(
                lc => lc.Identifier == "jc-leader-2" && lc.Label == AgentLabels.Error && lc.Added),
            timeout: TimeSpan.FromSeconds(10));

        // Snapshot the row and label count before the second reconciliation.
        string? originalErrorMessage;
        DateTimeOffset? originalCompletedAt;
        int originalLabelCount;
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var snapshot = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            Assert.NotNull(snapshot);
            originalErrorMessage = snapshot!.ErrorMessage;
            originalCompletedAt  = snapshot.CompletedAt;
        }
        originalLabelCount = Fixture.IssueProvider.LabelChanges
            .Count(lc => lc.Identifier == "jc-leader-2" && lc.Label == AgentLabels.Error && lc.Added);

        // Act: acquire leadership (clears _reconciledTerminalIds), then reconcile again.
        // Re-add the job to CreatedJobs before the second reconciliation so the loop
        // actually re-encounters the already-Failed item. FailRunCoreAsync.TryDeleteJobForRunAsync
        // removed it during the first reconciliation. Without re-adding it here,
        // FetchManagedJobsAsync returns an empty list, the loop does nothing, and all four
        // assertions below pass trivially — never exercising the 204 idempotent code path.
        Fixture.K8sClient.CreatedJobs[jobName] = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["app.kubernetes.io/component"]  = "agent-job",
                    ["caa/work-item-id"]             = id.ToString()
                },
                CreationTimestamp = DateTime.UtcNow
            },
            Status = new V1JobStatus()
        };
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName);

        Fixture.RealReconciliationLoop.OnLeadershipAcquired();
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // Assert: item is still Failed with identical row values (204 idempotent no-op).
        {
            await using var db = Fixture.DbContextFactory.CreateDbContext();
            var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
            Assert.NotNull(item);
            Assert.Equal(WorkItemStatus.Failed, item!.Status);
            Assert.Equal(originalErrorMessage, item.ErrorMessage);
            Assert.Equal(originalCompletedAt, item.CompletedAt);
        }

        // Assert: label was added exactly once — the 204 path does not trigger a second label swap.
        var finalLabelCount = Fixture.IssueProvider.LabelChanges
            .Count(lc => lc.Identifier == "jc-leader-2" && lc.Label == AgentLabels.Error && lc.Added);
        Assert.Equal(originalLabelCount, finalLabelCount);
        Assert.Equal(1, finalLabelCount);
    }
}
