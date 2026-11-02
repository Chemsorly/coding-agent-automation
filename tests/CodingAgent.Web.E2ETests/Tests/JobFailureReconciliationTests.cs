using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Infrastructure;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// E2E tests for <see cref="CodingAgent.JobController.Reconciliation.ReconciliationLoop"/>
/// failure paths: OOM, crash, drain/backoff-guard, and deadline.
///
/// <para>
/// All tests use the "jc-e2e" selector with no <see cref="FakeAgentClient"/> connected, so
/// <see cref="FakeJobController"/> ignores these work items.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "JobController")]
[Collection(E2ECollection.Name)]
public sealed class JobFailureReconciliationTests : HeadlessE2ETestBase
{
    public JobFailureReconciliationTests(E2EFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════════════
    // J3 — OOM kill
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Running item whose Job's last pod was OOMKilled is reconciled to Failed/ExitCodeFailure
    /// with the pod name, reason, and exit code in the error message.
    /// </summary>
    [Fact]
    public async Task WhenAnOomKilledPodFailsTheJob_ShouldFailWithExitCodeFailure_AndNameThePod()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(Fixture, "jc-oom-1");

        var podName = $"{jobName}-abc12";
        Fixture.K8sClient.AddTerminatedPod(jobName, podName, exitCode: 137, reason: "OOMKilled");
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName,
            reason: "BackoffLimitExceeded",
            message: "Job has reached the specified backoff limit");

        // ── Act ───────────────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert: WorkItem status, failure reason, error message ────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.ExitCodeFailure, item.FailureReason);
        Assert.Equal(
            $"Job has reached the specified backoff limit Last pod {podName}: OOMKilled (exit code 137).",
            item.ErrorMessage);

        // ── Assert: terminal label on the issue ───────────────────────────────────────
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges
                .Any(lc => lc.Identifier == "jc-oom-1" && lc.Added && lc.Label == AgentLabels.Error),
            timeout: TimeSpan.FromSeconds(10));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Crash exit-code naming
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Running item whose pod crashed with exit code 1 is reconciled to
    /// Failed/ExitCodeFailure with the pod name and exit code in the error message.
    /// </summary>
    [Fact]
    public async Task WhenACrashedPodFailsTheJob_ShouldNameTheExitCode()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(Fixture, "jc-crash-1");

        var podName = $"{jobName}-abc12";
        Fixture.K8sClient.AddTerminatedPod(jobName, podName, exitCode: 1, reason: "Error");
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName,
            reason: "BackoffLimitExceeded",
            message: "Job has reached the specified backoff limit");

        // ── Act ───────────────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert ────────────────────────────────────────────────────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.ExitCodeFailure, item.FailureReason);
        Assert.NotNull(item.ErrorMessage);
        Assert.EndsWith("Error (exit code 1).", item.ErrorMessage);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J8 — Job between pods: pod counters are not final
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Between a failed pod and its replacement, the Job shows Failed > 0 and Active == 0 with
    /// no terminal condition. The reconciliation loop must NOT mark the item Failed during this
    /// window. Once the terminal Failed condition is set, it reconciles normally.
    ///
    /// Mutation check: returning JobPhaseFailed when job.Status?.Failed > 0 from GetJobPhase
    /// makes the first-pass assertion (Status == Running) fail.
    /// </summary>
    [Fact]
    public async Task WhenAPodFailedButTheJobHasNoTerminalCondition_ShouldKeepTheItemRunning()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        const string issueIdentifier = "jc-backoff-1";
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(Fixture, issueIdentifier);

        Fixture.K8sClient.AddTerminatedPod(jobName, $"{jobName}-pod1", exitCode: 1, reason: "Error");

        // Simulate the between-pods state: Failed > 0, Active == 0, no terminal condition.
        var job = Fixture.K8sClient.CreatedJobs[jobName];
        job.Status!.Failed = 1;
        job.Status.Active = 0;
        // Status.Conditions remains empty (no Complete or Failed condition).

        // ── Act 1: reconcile with no terminal condition ───────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert 1: item still Running ──────────────────────────────────────────────
        await using var db1 = Fixture.DbContextFactory.CreateDbContext();
        var item1 = await db1.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item1);
        Assert.Equal(WorkItemStatus.Running, item1!.Status);
        Assert.Null(item1.ErrorMessage);
        Assert.DoesNotContain(jobName, Fixture.K8sClient.DeletedJobs);
        Assert.False(
            Fixture.IssueProvider.LabelChanges.Any(lc => lc.Identifier == issueIdentifier && lc.Added),
            "No label should have been added while the Job is still between pods");

        // ── Act 2: set terminal condition and reconcile again ─────────────────────────
        // The Job now has a Failed condition (BackoffLimitExceeded) — this is the terminal signal.
        // The first pass did not cache the GUID (_reconciledTerminalIds is empty), so this second
        // ReconcileOnceAsync processes the same item.
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName, reason: "BackoffLimitExceeded");

        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert 2: item now Failed ─────────────────────────────────────────────────
        await using var db2 = Fixture.DbContextFactory.CreateDbContext();
        var item2 = await db2.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item2);
        Assert.Equal(WorkItemStatus.Failed, item2!.Status);
        Assert.Equal(FailureReason.ExitCodeFailure, item2.FailureReason);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J8 — Drain / DisruptionTarget: PodFailurePolicy spec + no failure
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Verifies the Job spec carries BackoffLimit = 2, PodReplacementPolicy = "Failed", and
    /// a PodFailurePolicy that ignores DisruptionTarget pods. A pod with a DisruptionTarget
    /// condition and no terminal Job condition must not cause the item to be marked Failed.
    /// </summary>
    [Fact]
    public async Task WhenADrainDisruptsThePod_ShouldNotFailTheItem()
    {
        // ── Arrange: dispatch to get a real K8s Job spec ──────────────────────────────
        var workItemId = await InsertPendingWorkItemAsync("jc-drain-1", agentSelector: "jc-e2e");
        var expectedJobName = JobNameFactory.ForBrain(workItemId);

        using var apiClient = Fixture.CreateApiClient();
        var dispatchResponse = await apiClient.PostAsync(
            $"/api/work-items/{workItemId}/dispatch", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, dispatchResponse.StatusCode);

        // Wait for CreateJobAsync to record the Job — CreateJobAsync runs synchronously inside the
        // dispatch HTTP handler, but poll to guard against slow hosts under CI load (ScenarioA pattern).
        await WaitUntilAsync(
            () => Fixture.K8sClient.CreatedJobs.ContainsKey(expectedJobName),
            timeout: TimeSpan.FromSeconds(10));

        var job = Fixture.K8sClient.CreatedJobs[expectedJobName];

        // ── Assert spec: PodFailurePolicy ─────────────────────────────────────────────
        Assert.Equal(2, job.Spec?.BackoffLimit);
        Assert.Equal("Failed", job.Spec?.PodReplacementPolicy);
        Assert.NotNull(job.Spec?.PodFailurePolicy?.Rules);
        var rule = Assert.Single(job.Spec!.PodFailurePolicy!.Rules);
        Assert.Equal("Ignore", rule.Action);
        var condition = Assert.Single(rule.OnPodConditions!);
        Assert.Equal("DisruptionTarget", condition.Type);

        // ── Arrange: transition to Running and add run ────────────────────────────────
        var transitionService = Fixture.ApiServices.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Running);

        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = new IssueIdentifier("jc-drain-1"),
            IssueTitle = "Drain test issue",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "jc-e2e-test"
        });
        Fixture.RunService.AddRun(run);

        // ── Arrange: script a drained pod with DisruptionTarget condition ─────────────
        // Leave the Job with no terminal condition — Kubernetes would have applied the
        // Ignore PodFailurePolicy rule and not set a Failed condition.
        var pod = Fixture.K8sClient.AddTerminatedPod(
            expectedJobName, $"{expectedJobName}-drain1", exitCode: 1, reason: "Error");
        pod.Status ??= new V1PodStatus();
        pod.Status.Conditions = [new V1PodCondition { Type = "DisruptionTarget", Status = "True" }];

        // ── Act ───────────────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert: item still Running, Job not deleted ───────────────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Running, item!.Status);
        Assert.Null(item.ErrorMessage);
        Assert.DoesNotContain(expectedJobName, Fixture.K8sClient.DeletedJobs);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J9 — Deadline exceeded
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A Running item whose Job hit its activeDeadlineSeconds is reconciled to
    /// Failed/Timeout with the condition message as the error message.
    /// </summary>
    [Fact]
    public async Task WhenTheJobHitsItsDeadline_ShouldFailWithTimeout()
    {
        // ── Arrange ───────────────────────────────────────────────────────────────────
        var (id, jobName) = await JobReconciliationTestData.SeedRunningItemWithJobAsync(Fixture, "jc-deadline-1");

        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName,
            reason: "DeadlineExceeded",
            message: "Job was active longer than specified deadline");

        // ── Act ───────────────────────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(
            await Fixture.RealReconciliationLoop.FetchManagedJobsAsync(CancellationToken.None),
            CancellationToken.None);

        // ── Assert: WorkItem status, failure reason, error message ────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.Timeout, item.FailureReason);
        Assert.Equal("Job was active longer than specified deadline", item.ErrorMessage);

        // ── Assert: terminal label on the issue ───────────────────────────────────────
        await WaitUntilAsync(
            () => Fixture.IssueProvider.LabelChanges
                .Any(lc => lc.Identifier == "jc-deadline-1" && lc.Added && lc.Label == AgentLabels.Error),
            timeout: TimeSpan.FromSeconds(10));
    }
}
