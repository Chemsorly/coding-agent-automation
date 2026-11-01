using System.Net.Http.Json;
using CodingAgent.Api.Client;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using k8s.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

// Private DTO for deserialising the /dispatch response body in J5.
file sealed record DispatchBody(bool Dispatched, string? Reason);

/// <summary>
/// E2E tests that drive <see cref="WorkItemDispatchLoop.PollAndDispatchAsync"/> directly against
/// the real API host to exercise the production dispatch path end-to-end.
///
/// <para>
/// All existing E2E tests either bypass the loop entirely (<see cref="FakeJobController"/>) or
/// call <c>POST /dispatch</c> by hand with a fake agent client.  This class is the first to
/// drive <c>WorkItemDispatchLoop.PollAndDispatchAsync</c> itself, covering:
/// <list type="bullet">
///   <item>J1 — real K8s Job created from the template, consumed by a real in-process worker.</item>
///   <item>J2 — PVC pool exhaustion aborts the cycle; PVC freed after release.</item>
///   <item>J5 — concurrency limit stops only the matching selector.</item>
///   <item>J6 — K8s Job creation failure fails the item and aborts the cycle.</item>
///   <item>J7 — disabled profile → 410 on GET /assignment, item transitioned to Failed.</item>
///   <item>J8 — real worker receives 410 and exits gracefully.</item>
/// </list>
/// </para>
///
/// <para>
/// No <see cref="FakeAgentClient"/> is connected in any test, so
/// <see cref="FakeJobController"/> ignores the work items and cannot race the loop.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "JobController")]
[Collection(E2ECollection.Name)]
public sealed class ProductionDispatchE2ETests : HeadlessE2ETestBase
{
    public ProductionDispatchE2ETests(E2EFixture fixture) : base(fixture) { }

    private async Task PollAsync()
    {
        using var loop = new WorkItemDispatchLoop(Fixture.WorkItems, null, Serilog.Log.Logger);
        await loop.PollAndDispatchAsync(CancellationToken.None);
    }

    private async Task<WorkItemEntity> ReadAsync(Guid id)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == id);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J1 — loop dispatches a pending item, creates a K8s Job, real worker completes it
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenAPendingItemIsPolled_ShouldCreateTheJobFromItsTemplate_AndARealWorkerCompletesIt()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        const string issueId = "7001";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Production dispatch J1",
            Description = "## Requirements\nTest production dispatch.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-dispatch",
            Name = "Dispatch repo",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-dispatch-j1",
            DisplayName = "Dispatch J1 profile",
            JobTemplate = E2ERouting.Key(["jc-e2e"]),
            Enabled = true
        });

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen()
            .EnqueueCodeGen();

        Fixture.QualityGateValidator.AlwaysPass();

        // Subscribe BEFORE DistributeDirectlyAsync so no event is missed.
        var jobTcs = new TaskCompletionSource<V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<V1Job>? handler = null;
        handler = job =>
        {
            Fixture.K8sClient.WorkItemJobCreated -= handler;
            jobTcs.TrySetResult(job);
        };
        Fixture.K8sClient.WorkItemJobCreated += handler;

        // DistributeDirectlyAsync writes a proper JobDistributionRequest payload so that
        // GET /assignment can build the full assignment message.
        var result = await DistributeDirectlyAsync(issueId, agentSelector: "jc-e2e");
        Assert.True(result.Success, $"DistributeDirectlyAsync failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        // ── Act ───────────────────────────────────────────────────────────────
        // PollAsync calls POST /dispatch, which fires WorkItemJobCreated synchronously inside
        // CreateJobAsync, then marks the item Dispatched.
        await PollAsync();

        var job = await jobTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var agentId = job.Spec!.Template!.Spec!.Containers![0].Env!
            .First(e => e.Name == "AGENT_ID").Value;

        // Start the real worker AFTER PollAsync so the item is already Dispatched and
        // GET /assignment returns the full assignment message.
        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        // ── Assert ────────────────────────────────────────────────────────────
        var item = await ReadAsync(workItemId);

        Assert.Equal(JobNameFactory.ForBrain(workItemId), job.Metadata!.Name);
        Assert.Equal(job.Metadata!.Name, agentId);

        var labels = job.Metadata!.Labels;
        Assert.NotNull(labels);
        Assert.True(labels.TryGetValue("caa/work-item-id", out var labelWorkItemId));
        Assert.Equal(workItemId.ToString(), labelWorkItemId);
        Assert.True(labels.TryGetValue("app.kubernetes.io/component", out var componentLabel));
        Assert.Equal("agent-job", componentLabel);

        Assert.Equal("chemsorly/coding-agent:opencode-dotnet10-latest",
            job.Spec!.Template!.Spec!.Containers![0].Image);
        Assert.Equal(item.TimeoutSeconds + 60, job.Spec!.ActiveDeadlineSeconds);
        Assert.Equal(job.Metadata!.Name, item.K8sJobName);
        Assert.Equal(agentId, item.AssignedAgentId);

        Assert.Contains(nameof(IRepositoryProvider.CreatePullRequestAsync),
            Fixture.RepositoryProvider.MethodCalls);

        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        Assert.Contains(AgentLabels.Done, issue.Labels);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J2 — PVC pool exhausted: cycle aborts; PVC freed after release
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenThePvcPoolIsExhausted_ShouldKeepTheItemPending_AbortTheCycle_AndDispatchAfterRelease()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        // Insert sequentially (not Task.WhenAll) to guarantee CreatedAt ordering.
        var idA = await InsertPendingWorkItemAsync("kiro-a", "kiro,dotnet");
        var idB = await InsertPendingWorkItemAsync("kiro-b", "kiro,dotnet");
        var idC = await InsertPendingWorkItemAsync("kiro-c", "kiro,dotnet");
        var idD = await InsertPendingWorkItemAsync("other-d", "jc-e2e");

        // ── Act: first poll ───────────────────────────────────────────────────
        await PollAsync();

        // ── Assert: first poll ────────────────────────────────────────────────
        var a = await ReadAsync(idA);
        var b = await ReadAsync(idB);
        var c = await ReadAsync(idC);
        var d = await ReadAsync(idD);

        // a and b claim the two PVCs; c gets 503 (stays Pending); d is never reached (cycle aborted).
        Assert.Equal(WorkItemStatus.Dispatched, a.Status);
        Assert.Equal(WorkItemStatus.Dispatched, b.Status);
        Assert.NotNull(a.ClaimedPvcName);
        Assert.NotNull(b.ClaimedPvcName);
        Assert.Equal(
            new HashSet<string> { "fake-pvc-0", "fake-pvc-1" },
            new HashSet<string> { a.ClaimedPvcName!, b.ClaimedPvcName! });
        Assert.Equal(WorkItemStatus.Pending, c.Status);
        Assert.Null(c.ClaimedPvcName);
        Assert.Equal(WorkItemStatus.Pending, d.Status);
        Assert.Equal(2, Fixture.K8sClient.CreatedJobs.Count);

        // ── Act: direct 503 assertion (BEFORE releasing the PVC) ─────────────
        // Must happen while the pool is still exhausted; after the release c would dispatch.
        var directResult = await Fixture.WorkItems.DispatchPendingAsync(idC);
        Assert.Equal(DispatchPendingResult.Transient, directResult);

        // ── Act: free PVC from a ──────────────────────────────────────────────
        // Both the item AND its Job must be terminal for the PVC to be freed.
        await Fixture.WorkItems.PostStatusAsync(idA, new WorkItemStatusUpdate
        {
            Status = "Failed",
            ErrorMessage = "freed",
            FailureReason = "InfrastructureFailure"
        });
        await Fixture.K8sClient.SimulateWorkItemJobFailedAsync(JobNameFactory.ForBrain(idA));

        // ── Act: second poll ──────────────────────────────────────────────────
        await PollAsync();

        // ── Assert: second poll ───────────────────────────────────────────────
        c = await ReadAsync(idC);
        d = await ReadAsync(idD);
        b = await ReadAsync(idB);

        Assert.Equal(WorkItemStatus.Dispatched, c.Status);
        Assert.Equal(a.ClaimedPvcName, c.ClaimedPvcName);  // reused freed PVC
        Assert.Equal(WorkItemStatus.Dispatched, d.Status);
        Assert.Equal(WorkItemStatus.Dispatched, b.Status);  // unchanged
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J5 — concurrency limit stops only the matching selector
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenASelectorIsAtItsConcurrencyLimit_ShouldDeferOnlyThatSelector()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        // jc-limit-e2e has maxConcurrent: 1 (set in E2ETestDefaults.InstallJobTemplates).
        var idL1 = await InsertPendingWorkItemAsync("limit-1", "jc-limit-e2e");
        var idL2 = await InsertPendingWorkItemAsync("limit-2", "jc-limit-e2e");
        var idL3 = await InsertPendingWorkItemAsync("limit-3", "jc-limit-e2e");
        var idO1 = await InsertPendingWorkItemAsync("other-1", "jc-e2e");

        // ── Act ───────────────────────────────────────────────────────────────
        await PollAsync();

        // ── Assert: loop results ──────────────────────────────────────────────
        // limit-1 dispatches; limit-2 gets PermanentRejection (concurrency gate), which
        // stops the jc-limit-e2e selector; limit-3 is skipped; other-1 (jc-e2e) dispatches.
        Assert.Equal(WorkItemStatus.Dispatched, (await ReadAsync(idL1)).Status);
        Assert.Equal(WorkItemStatus.Dispatched, (await ReadAsync(idO1)).Status);
        Assert.Equal(WorkItemStatus.Pending, (await ReadAsync(idL2)).Status);
        Assert.Equal(WorkItemStatus.Pending, (await ReadAsync(idL3)).Status);

        var createdKeys = Fixture.K8sClient.CreatedJobs.Keys.ToHashSet();
        Assert.Contains(JobNameFactory.ForBrain(idL1), createdKeys);
        Assert.Contains(JobNameFactory.ForBrain(idO1), createdKeys);
        Assert.Equal(2, createdKeys.Count);

        // ── Act: direct dispatch of limit-2 ──────────────────────────────────
        using var client = Fixture.CreateApiClient();
        var resp = await client.PostAsync($"/api/work-items/{idL2}/dispatch", null);
        Assert.Equal(System.Net.HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<DispatchBody>(PipelineJsonOptions.Default);
        Assert.NotNull(body);
        Assert.False(body!.Dispatched);
        Assert.Equal("concurrency_limit", body.Reason);
        Assert.Equal(WorkItemStatus.Pending, (await ReadAsync(idL2)).Status);

        // ── Assert: DispatchPendingAsync also returns PermanentRejection ──────
        var directResult = await Fixture.WorkItems.DispatchPendingAsync(idL3);
        Assert.Equal(DispatchPendingResult.PermanentRejection, directResult);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J6 — K8s Job creation failure: item fails, cycle aborts, next item dispatches next cycle
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenJobCreationFails_ShouldFailThatItem_AbortTheCycle_AndDispatchTheNextOnTheNextCycle()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        var idFail = await InsertPendingWorkItemAsync("create-fail", "kiro,dotnet");
        var idNext = await InsertPendingWorkItemAsync("create-next", "kiro,dotnet");
        Fixture.K8sClient.FailNextCreate = true;

        // ── Act: first poll ───────────────────────────────────────────────────
        await PollAsync();

        // ── Assert: first poll ────────────────────────────────────────────────
        var itemFail = await ReadAsync(idFail);
        Assert.Equal(WorkItemStatus.Failed, itemFail.Status);
        Assert.Equal(FailureReason.InfrastructureFailure, itemFail.FailureReason);
        Assert.NotNull(itemFail.ErrorMessage);
        Assert.StartsWith("K8s Job creation failed:", itemFail.ErrorMessage);
        Assert.Null(itemFail.ClaimedPvcName);

        // Cycle aborted after the 503; create-next was never reached.
        Assert.Equal(WorkItemStatus.Pending, (await ReadAsync(idNext)).Status);

        // FakeKubernetesJobClient throws BEFORE recording the job, so CreatedJobs is empty.
        Assert.Empty(Fixture.K8sClient.CreatedJobs);

        // ── Act: second poll ──────────────────────────────────────────────────
        await PollAsync();

        // ── Assert: second poll ───────────────────────────────────────────────
        var itemNext = await ReadAsync(idNext);
        Assert.Equal(WorkItemStatus.Dispatched, itemNext.Status);
        Assert.NotNull(itemNext.ClaimedPvcName);

        // create-fail is not re-queued (GetPendingAsync only returns Pending items).
        Assert.Equal(WorkItemStatus.Failed, (await ReadAsync(idFail)).Status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J7 — disabled profile produces 410 on GET /assignment
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenTheRoutedProfileIsDisabledBeforeAssignment_ShouldFailTheItemAndAnswerGone()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        const string issueId = "7002";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Profile refused J7",
            Description = "## Requirements\nTest profile refusal.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-dispatch-j7",
            Name = "Dispatch repo J7",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        var profile = new AgentProfile
        {
            Id = "profile-refused",
            DisplayName = "Refused profile",
            JobTemplate = E2ERouting.Key(["jc-e2e"]),
            Enabled = true
        };
        await Fixture.SeedRoutedProfileAsync(profile);

        var result = await DistributeDirectlyAsync(issueId, agentSelector: "jc-e2e");
        Assert.True(result.Success, $"DistributeDirectlyAsync failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        await PollAsync();
        Assert.Equal(WorkItemStatus.Dispatched, (await ReadAsync(workItemId)).Status);

        // Disable the profile AFTER dispatch so POST /dispatch is unaffected.
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            E2ERouting.Converted(profile) with { Enabled = false }, CancellationToken.None);

        // ── Act ───────────────────────────────────────────────────────────────
        using var client = Fixture.CreateApiClient();
        var resp = await client.GetAsync($"/api/work-items/{workItemId}/assignment");

        // ── Assert ────────────────────────────────────────────────────────────
        Assert.Equal(System.Net.HttpStatusCode.Gone, resp.StatusCode);

        var item = await ReadAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed, item.Status);
        Assert.Equal(FailureReason.InfrastructureFailure, item.FailureReason);
        Assert.NotNull(item.ErrorMessage);
        Assert.Contains("no longer exists", item.ErrorMessage);

        // Label swap fires asynchronously; poll until agent:error appears.
        await WaitUntilAsync(async () =>
        {
            var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
            return issue.Labels.Contains(AgentLabels.Error);
        }, timeout: TimeSpan.FromSeconds(10));
    }

    // ═══════════════════════════════════════════════════════════════════════
    // J8 — real worker receives 410 and exits gracefully without running the pipeline
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task WhenARealWorkerIsRefusedAtAssignment_ShouldExitWithoutRunningThePipeline()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        const string issueId = "7003";
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Profile refused J8",
            Description = "## Requirements\nTest worker refusal.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "template-dispatch-j8",
            Name = "Dispatch repo J8",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        var profile = new AgentProfile
        {
            Id = "profile-refused-j8",
            DisplayName = "Refused profile J8",
            JobTemplate = E2ERouting.Key(["jc-e2e"]),
            Enabled = true
        };
        await Fixture.SeedRoutedProfileAsync(profile);

        // Subscribe BEFORE DistributeDirectlyAsync to capture the job created by PollAsync.
        var jobTcs = new TaskCompletionSource<V1Job>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<V1Job>? handler = null;
        handler = job =>
        {
            Fixture.K8sClient.WorkItemJobCreated -= handler;
            jobTcs.TrySetResult(job);
        };
        Fixture.K8sClient.WorkItemJobCreated += handler;

        var result = await DistributeDirectlyAsync(issueId, agentSelector: "jc-e2e");
        Assert.True(result.Success, $"DistributeDirectlyAsync failed: {result.ErrorMessage}");
        var workItemId = Guid.Parse(result.WorkItemId!);

        await PollAsync();
        var job = await jobTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var agentId = job.Spec!.Template!.Spec!.Containers![0].Env!
            .First(e => e.Name == "AGENT_ID").Value;

        // Disable the profile AFTER PollAsync (dispatch succeeded) and BEFORE starting the worker.
        await Fixture.ConfigStore.SaveAgentProfileAsync(
            E2ERouting.Converted(profile) with { Enabled = false }, CancellationToken.None);

        // ── Act ───────────────────────────────────────────────────────────────
        // The worker calls GET /assignment, receives 410 (profile disabled), and exits 0.
        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(20));

        // ── Assert ────────────────────────────────────────────────────────────
        var item = await ReadAsync(workItemId);
        Assert.Equal(WorkItemStatus.Failed, item.Status);
        Assert.NotNull(item.ErrorMessage);
        Assert.Contains("no longer exists", item.ErrorMessage);

        // No comments posted to the issue, no PR created.
        Assert.Equal(0, Fixture.IssueProvider.PostedComments.Count(c => c.Identifier == issueId));
        Assert.DoesNotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            Fixture.RepositoryProvider.MethodCalls);
    }
}
