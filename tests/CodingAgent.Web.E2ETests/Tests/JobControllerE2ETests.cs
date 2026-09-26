using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Shared label arrays used across JobControllerE2ETests to avoid CA1861
/// (constant array arguments should be 'static readonly' fields).
/// </summary>
file static class TestLabels
{
    public static readonly string[] Enhancement = ["enhancement"];
    public static readonly string[] Jce2e = ["jc-e2e"];
    public static readonly string[] EnhancementAndAgentNext = ["enhancement", "agent:next"];
}

/// <summary>
/// E2E tests for the real JobController reconciliation loop hosted in-process.
///
/// <para>
/// All existing E2E tests dispatch through <see cref="FakeJobController"/>, which never exercises
/// <c>ReconciliationLoop</c>. This class addresses that gap by hosting
/// <c>CodingAgent.JobController</c> against the real API host (via
/// <see cref="JobControllerE2EWebApplicationFactory"/>) and driving its reconciliation methods
/// directly.
/// </para>
///
/// <para>
/// Three scenarios are covered:
/// <list type="bullet">
///   <item><b>A — dispatch smoke</b>: full round-trip from Pending through K8s Job creation,
///     agent completion, and orphan cleanup.</item>
///   <item><b>B — orphan sweep vs fresh Job</b>: regression test for issue #2950. The
///     <c>CleanupOrphansAsync</c> method must not delete a job that was just created while its
///     WorkItem is still between create-Job and mark-Dispatched.</item>
///   <item><b>C — Job failure reconciliation</b>: a failed K8s Job is reconciled to
///     <c>Failed/InfrastructureFailure</c> when no agent ever ran.</item>
/// </list>
/// </para>
///
/// <para>
/// Isolation: all tests use the <c>"jc-e2e"</c> agent selector, which no other test class uses
/// and for which no <see cref="FakeAgentClient"/> is registered with <see cref="FakeJobController"/>.
/// The existing FakeJobController poll loop therefore ignores these work items.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "JobController")]
[Collection(E2ECollection.Name)]
public sealed class JobControllerE2ETests : HeadlessE2ETestBase
{
    public JobControllerE2ETests(E2EFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario A — dispatch smoke test
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Full round-trip: Pending WorkItem → K8s Job created via POST /api/work-items/{id}/dispatch
    /// → FakeAgentClient bootstraps and completes the job → WorkItem reaches Succeeded →
    /// ReconciliationLoop.CleanupOrphansAsync deletes the stale Job.
    /// </summary>
    [Fact]
    public async Task ScenarioA_DispatchSmoke_FullRoundTrip()
    {
        // ── Arrange: seed issue and agent profile ─────────────────────────────────────
        // DistributeDirectlyAsync writes a proper JobDistributionRequest payload so that
        // StartAssignedWorkItemAsync (GET /assignment) can build the assignment message.
        // InsertPendingWorkItemAsync uses Payload = "{}" which causes GetAssignment to
        // return 503 (AssignmentEnricher can't find a profile for the selector).
        const string issueIdentifier = "jc-e2e/smoke-1";
        Fixture.IssueProvider.Issues.Add(new Pipeline.Models.IssueDetail
        {
            Identifier = issueIdentifier,
            Title = "Smoke test issue",
            Description = "## Requirements\nSmoke test\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = TestLabels.Enhancement
        });

        // Seed an AgentProfile so AssignmentEnricher can resolve the jc-e2e selector.
        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id = "profile-jc-e2e",
            DisplayName = "JobController E2E Profile",
            MatchLabels = TestLabels.Jce2e,
            AgentProviderConfigId = "agent-e2e",
            Enabled = true
        }, CancellationToken.None);

        // ── Act: distribute and dispatch ──────────────────────────────────────────────
        var result = await DistributeDirectlyAsync(issueIdentifier, agentSelector: "jc-e2e");
        Assert.True(result.Success, $"DistributeDirectlyAsync failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);
        var expectedJobName = JobNameFactory.ForBrain(workItemId);

        // Call POST /api/work-items/{id}/dispatch directly (operator-authenticated, API host).
        // This is what WorkItemDispatchLoop does in production but it is not running in the harness.
        using var apiClient = Fixture.CreateApiClient();
        var dispatchResponse = await apiClient.PostAsync(
            $"/api/work-items/{workItemId}/dispatch", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, dispatchResponse.StatusCode);

        // Wait for the K8s Job to appear in CreatedJobs.
        // CreateJobAsync runs synchronously inside the dispatch HTTP call so the job should
        // already be there, but poll to be safe.
        await WaitUntilAsync(
            () => Fixture.K8sClient.CreatedJobs.ContainsKey(expectedJobName),
            timeout: TimeSpan.FromSeconds(10),
            pollInterval: TimeSpan.FromMilliseconds(50));

        var createdJob = Fixture.K8sClient.CreatedJobs[expectedJobName];
        Assert.NotNull(createdJob);
        var jobName = createdJob.Metadata?.Name;
        Assert.NotNull(jobName);

        // ── Assert: job naming and labels ─────────────────────────────────────────────
        // JobNameFactory.ForBrain produces "caa-{first-8-hex}" (12 chars).
        // Do NOT use the legacy "caa-agent-" format from unit test helpers.
        Assert.Equal(expectedJobName, jobName);

        var labels = createdJob.Metadata!.Labels;
        Assert.NotNull(labels);
        Assert.True(labels.TryGetValue("caa/work-item-id", out var labelWorkItemId));
        Assert.Equal(workItemId.ToString(), labelWorkItemId);
        Assert.True(labels.TryGetValue("app.kubernetes.io/managed-by", out var managedBy));
        Assert.Equal("caa-orchestrator", managedBy);

        // ── Assert: AGENT_ID env var ──────────────────────────────────────────────────
        var container = createdJob.Spec?.Template?.Spec?.Containers?.FirstOrDefault();
        Assert.NotNull(container);
        var agentIdEnv = container!.Env?.FirstOrDefault(e => e.Name == "AGENT_ID");
        Assert.NotNull(agentIdEnv);
        Assert.Equal(jobName, agentIdEnv!.Value);

        // ── Act: bootstrap FakeAgentClient as the pod would ───────────────────────────
        // Use Task.Run so the connection races in parallel with any dispatch path still in flight.
        // The agent must connect and call StartAssignedWorkItemAsync to re-register as active
        // (sets ActiveJobId in registry — required for [RequiresActiveJob] endpoints).
        // TODO [WARNING]: Scenario A's isolation claim is partially false. Connecting a
        // FakeAgentClient with selector "jc-e2e" registers it as an Idle agent on the shared API
        // hub, so the FakeJobController background loop (250 ms tick) can match it and call
        // StartAssignedWorkItemAsync concurrently with the test's own call below. The double-
        // bootstrap survives today because TrySetResult/TryAdd are idempotent, but any future
        // change that makes the terminal post non-idempotent could flip the WorkItem to Failed
        // and break the Succeeded assertion. Fix: use a distinct agent selector for Scenario A
        // that FakeJobController has no template for (e.g. "jc-e2e-a"), or call
        // Fixture.JobController.ForgetAllInFlight() / pause the controller before connecting.
        await using var fakeAgent = new FakeAgentClient(jobName, "jc-e2e");
        var connectTask = Task.Run(async () =>
        {
            await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);
            await fakeAgent.StartAssignedWorkItemAsync(workItemId);
        });

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await connectTask; // ensure connect completed without error

        // ── Act: accept and complete the job ──────────────────────────────────────────
        await fakeAgent.AcceptJobAsync(assignment.JobId);
        await fakeAgent.ReportCompletionAsync(assignment.JobId, new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow,
            RetryCount = 0,
            FilesChangedCount = 1,
            LinesAdded = 5,
            LinesRemoved = 1,
            AnalysisRecommendation = AnalysisGateResult.Ready,
            AnalysisConcerns = Array.Empty<string>(),
            AnalysisBlockingIssues = Array.Empty<string>(),
            BlacklistedFilesDetected = Array.Empty<string>(),
            CodeReviewAgentsRun = Array.Empty<string>(),
            PullRequestUrl = "https://github.com/jc-e2e/repo/pull/1"
        });

        // ── Assert: WorkItem is Succeeded ─────────────────────────────────────────────
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));

        // ── Act: reconcile via the real ReconciliationLoop ────────────────────────────
        // ReconcileOnceAsync sees the Succeeded K8s Job condition and calls PostStatusAsync
        // (no-op since WorkItem is already Succeeded) then caches the ID.
        // TODO [WARNING]: This ReconcileOnceAsync call is actually a no-op. The agent-completion
        // hub path transitions the WorkItem to Succeeded but never sets a Succeeded condition on
        // the K8s Job object in CreatedJobs, so GetJobPhase returns "Active" and HandleJobAsync
        // takes no action. To actually exercise the success reconciliation path, set a Complete
        // condition on createdJob (mirroring SimulateWorkItemJobFailedAsync) before this call
        // and assert the resulting behaviour (e.g. _reconciledTerminalIds populated, no
        // spurious status transition).
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(CancellationToken.None);

        // Age the job past the 600s retention window so CleanupOrphansAsync deletes it.
        // (In production this is handled by K8s TTL; in the test we set CompletionTime manually.)
        if (createdJob.Status is null) createdJob.Status = new V1JobStatus();
        createdJob.Status.CompletionTime = DateTime.UtcNow.AddSeconds(-700);

        // ── Act: orphan cleanup ───────────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.CleanupOrphansAsync(CancellationToken.None);

        // ── Assert: job was deleted ───────────────────────────────────────────────────
        Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);
        // TODO [WARNING]: After reconciliation and deletion, there is no assertion confirming the
        // WorkItem record is still Succeeded in the database. If ReconcileOnceAsync
        // mis-classified the job and wrote an unexpected status transition, it would go unnoticed.
        // Add: Assert.Equal(WorkItemStatus.Succeeded, item.Status) after reading the DB record.
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario B — orphan sweep must NOT delete a fresh Job (#2950 regression)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Regression test for issue #2950: <c>CleanupOrphansAsync</c> must not delete a K8s Job
    /// that was just created while its WorkItem is still between create-Job and mark-Dispatched.
    ///
    /// <para>
    /// The <see cref="FakeKubernetesJobClient.AfterCreateDelay"/> gate pauses
    /// <c>CreateJobAsync</c> so the test can call <c>CleanupOrphansAsync</c> while the WorkItem
    /// is still <c>Pending</c> (not yet in the active set). The #2950 fix stamps
    /// <c>CreationTimestamp</c> on newly-created jobs, giving them a 600-second retention window
    /// that prevents premature deletion.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ScenarioB_OrphanSweep_DoesNotDeleteFreshJob_WithFix()
    {
        // ── Arrange ──────────────────────────────────────────────────────────────────
        // Set the gate BEFORE dispatch. CreateJobAsync will record the job then await this TCS.
        Fixture.K8sClient.AfterCreateDelay = new TaskCompletionSource();

        var workItemId = await InsertPendingWorkItemAsync("jc-e2e/orphan-b1", agentSelector: "jc-e2e");

        // ── Act: start dispatch in background ─────────────────────────────────────────
        // CreateJobAsync fires synchronously, records the job with CreationTimestamp = UtcNow,
        // then blocks on AfterCreateDelay. The WorkItem is still Pending from the API's view
        // (the Dispatched write hasn't happened yet).
        using var apiClient = Fixture.CreateApiClient();
        var dispatchTask = Task.Run(async () =>
            await apiClient.PostAsync($"/api/work-items/{workItemId}/dispatch", null));

        // Wait until the job is recorded (CreateJobAsync ran up to the gate)
        await WaitUntilAsync(
            () => !Fixture.K8sClient.CreatedJobs.IsEmpty,
            timeout: TimeSpan.FromSeconds(10));

        // TODO [WARNING]: Keys.First() is non-deterministic when CreatedJobs contains entries from
        // other concurrently-running tests in the same E2ECollection. Replace with:
        //   var expectedJobName = JobNameFactory.ForBrain(workItemId);
        //   WaitUntilAsync(() => Fixture.K8sClient.CreatedJobs.ContainsKey(expectedJobName), ...)
        //   var jobName = expectedJobName;
        // (matching the deterministic pattern used in Scenario A).
        var jobName = Fixture.K8sClient.CreatedJobs.Keys.First();

        // ── Act: run orphan cleanup directly while the WorkItem is still Pending ──────
        // Do NOT call ReconcileOnceAsync or OnPollCycleAsync — those also invoke
        // EnforceDispatchedTimeoutAsync, which could race and fail the WorkItem.
        await Fixture.RealReconciliationLoop.CleanupOrphansAsync(CancellationToken.None);

        // ── Assert: job was NOT deleted (CreationTimestamp is within the 600s window) ──
        Assert.DoesNotContain(jobName, Fixture.K8sClient.DeletedJobs);

        // ── Assert: WorkItem is not Failed ────────────────────────────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.NotEqual(WorkItemStatus.Failed, item!.Status);

        // ── Cleanup: release the gate so dispatch finishes ────────────────────────────
        Fixture.K8sClient.AfterCreateDelay.SetResult();
        await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10));

        // Confirm WorkItem reached Dispatched after the gate was released
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Dispatched, TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Demonstrates that temporarily reverting the #2950 fix (clearing <c>CreationTimestamp</c>
    /// from the just-created Job) causes <c>CleanupOrphansAsync</c> to delete the Job during the
    /// race window.
    ///
    /// <para>
    /// This test acts as the "revert makes scenario B fail" acceptance criterion. If the
    /// <c>CreationTimestamp</c> fallback in <c>CleanupOrphansAsync</c> were removed, the
    /// fresh Job would have no timestamp anchor and would be deleted immediately.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ScenarioB_WithoutFix_JobIsDeleted_WhenCreationTimestampCleared()
    {
        // ── Arrange ──────────────────────────────────────────────────────────────────
        Fixture.K8sClient.AfterCreateDelay = new TaskCompletionSource();

        var workItemId = await InsertPendingWorkItemAsync("jc-e2e/orphan-b2", agentSelector: "jc-e2e");

        // ── Act: start dispatch in background ─────────────────────────────────────────
        using var apiClient = Fixture.CreateApiClient();
        var dispatchTask = Task.Run(async () =>
            await apiClient.PostAsync($"/api/work-items/{workItemId}/dispatch", null));

        // Wait until the job is recorded
        await WaitUntilAsync(
            () => !Fixture.K8sClient.CreatedJobs.IsEmpty,
            timeout: TimeSpan.FromSeconds(10));

        // TODO [WARNING]: Keys.First() is non-deterministic when CreatedJobs contains entries from
        // other concurrently-running tests in the same E2ECollection. Replace with:
        //   var expectedJobName = JobNameFactory.ForBrain(workItemId);
        //   WaitUntilAsync(() => Fixture.K8sClient.CreatedJobs.ContainsKey(expectedJobName), ...)
        //   var jobName = expectedJobName;
        // (matching the deterministic pattern used in Scenario A).
        var jobName = Fixture.K8sClient.CreatedJobs.Keys.First();

        // ── Simulate pre-fix state: clear CreationTimestamp ───────────────────────────
        // This is what happened before #2950: the fake did not set CreationTimestamp, so the
        // orphan sweep had no anchor and deleted the job immediately.
        if (Fixture.K8sClient.CreatedJobs.TryGetValue(jobName, out var freshJob))
        {
            if (freshJob.Metadata is not null)
                freshJob.Metadata.CreationTimestamp = null;
        }

        // ── Act: run orphan cleanup ───────────────────────────────────────────────────
        await Fixture.RealReconciliationLoop.CleanupOrphansAsync(CancellationToken.None);

        // ── Assert: job WAS deleted (no timestamp anchor → no retention window) ───────
        Assert.Contains(jobName, Fixture.K8sClient.DeletedJobs);

        // ── Cleanup: release the gate so dispatch finishes (will fail — job was deleted) ─
        Fixture.K8sClient.AfterCreateDelay.SetResult();
        // Dispatch will return a non-200 (the K8s job creation was rolled back or the
        // endpoint received the delete). Await without asserting status.
        // TODO [WARNING]: The speculative comment above about a "non-200" is inaccurate. The
        // actual outcome is a 200 with an orphaned Dispatched WorkItem row (the job was deleted
        // but ExecuteDispatchLifecycleAsync still sets Dispatched). Remove the speculative comment
        // or assert the concrete end state to make the test's intent unambiguous.
        try { await dispatchTask.WaitAsync(TimeSpan.FromSeconds(10)); } catch { /* expected */ }
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Scenario C — Job failure reconciliation
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A failed K8s Job for a <c>Dispatched</c> WorkItem (no agent ever ran) is reconciled to
    /// <c>Failed</c> with <c>FailureReason.InfrastructureFailure</c>, and the issue receives
    /// exactly one terminal label swap.
    /// </summary>
    [Fact]
    public async Task ScenarioC_JobFailureReconciliation_InfrastructureFailure()
    {
        const string issueIdentifier = "jc-e2e/failure-c1";

        // ── Arrange: seed InMemoryIssueProvider ───────────────────────────────────────
        // Required so the label-swap callback triggered by PostStatusAsync has an issue to act on.
        Fixture.IssueProvider.Issues.Add(new Pipeline.Models.IssueDetail
        {
            Identifier = issueIdentifier,
            Title = "Test failure issue",
            Description = "## Requirements\nTrigger infrastructure failure\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = TestLabels.EnhancementAndAgentNext
        });

        // ── Arrange: insert WorkItem ──────────────────────────────────────────────────
        var workItemId = await InsertPendingWorkItemAsync(issueIdentifier, agentSelector: "jc-e2e");

        // ── Arrange: transition WorkItem to Dispatched with K8sJobName set ─────────────
        // Pattern from K8sModeTests.K8sMode_WorkItemCreatedAsDispatched_K8sJobAlreadyRunning.
        var jobName = JobNameFactory.ForBrain(workItemId);
        var transitionService = Fixture.ApiServices
            .GetRequiredService<WorkItemTransitionService>();
        var transitioned = await transitionService.TransitionAsync(
            workItemId, WorkItemStatus.Dispatched,
            w =>
            {
                w.DispatchedAt = DateTimeOffset.UtcNow;
                w.K8sJobName = jobName;
            });
        Assert.True(transitioned, "WorkItem must transition to Dispatched");

        // ── Arrange: add a PipelineRun so the label-swap has a run to act on ──────────
        // ReconciliationLoop.PostStatusAsync → WorkItemStatusTransitionService.FailRunWithLabelAsync
        // → RunLifecycleManager.FailRunCoreAsync → _runService.RemoveRun(runId).
        // Without a matching run in the in-memory service, FailRunCoreAsync returns null silently
        // and no label swap fires.
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = workItemId.ToString(),
            IssueIdentifier = new IssueIdentifier(issueIdentifier),
            IssueTitle = "Test failure issue",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "jc-e2e-test"
        });
        Fixture.RunService.AddRun(run);

        // ── Arrange: add a fake Job to the client ─────────────────────────────────────
        // ReconciliationLoop.ReconcileOnceAsync calls ListJobsAsync and processes each job.
        // We add the job directly to CreatedJobs with the required labels.
        var fakeJob = new V1Job
        {
            Metadata = new V1ObjectMeta
            {
                Name = jobName,
                Labels = new Dictionary<string, string>
                {
                    ["app.kubernetes.io/managed-by"] = "caa-orchestrator",
                    ["caa/work-item-id"] = workItemId.ToString()
                },
                CreationTimestamp = DateTime.UtcNow
            },
            Status = new V1JobStatus()
        };
        Fixture.K8sClient.CreatedJobs[jobName] = fakeJob;

        // ── Act: mark the K8s Job as Failed ──────────────────────────────────────────
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(jobName);

        // ── Act: reconcile ────────────────────────────────────────────────────────────
        // ReconciliationLoop.HandleJobAsync detects Failed condition →
        // ClassifyJobFailureReasonAsync calls GetStatusAsync → WorkItem is Dispatched
        //   → failureReason = "InfrastructureFailure"
        // HandleJobCompletedAsync calls PostStatusAsync(Failed, InfrastructureFailure)
        // WorkItemStatusTransitionService triggers label-swap via InMemoryIssueProvider.
        await Fixture.RealReconciliationLoop.ReconcileOnceAsync(CancellationToken.None);

        // ── Assert: WorkItem is Failed with InfrastructureFailure ────────────────────
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item!.Status);
        Assert.Equal(FailureReason.InfrastructureFailure, item.FailureReason);

        // ── Assert: exactly one terminal label swap on the issue ──────────────────────
        // PostStatusAsync triggers WorkItemStatusTransitionService which calls
        // IIssueProvider.SwapLabelsAsync → InMemoryIssueProvider.LabelChanges records it.
        // The terminal label (agent:error) must be added exactly once — no double-post.
        var labelChanges = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == issueIdentifier && lc.Added)
            .ToList();
        // TODO [WARNING]: Assert.Single only confirms that exactly one label was added; it does
        // not assert which label. A regression that swapped to the wrong terminal label would
        // still pass. Add: Assert.Equal("<expected-terminal-label>", labelChanges.Single().Label)
        // to fully enforce the "infrastructure category → exactly one terminal label" requirement.
        Assert.Single(labelChanges);
    }
}
