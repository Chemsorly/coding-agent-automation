using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// What happens when Kubernetes replaces an agent pod after a drain, against the E2E API host.
///
/// <para>
/// A drained pod exits 143 and records no outcome, so its work item is still <c>Running</c> when the replacement pod
/// starts. The replacement reports <c>Running</c> again. The API answers 200 (<c>Transitioned</c>), the client
/// accepts it and the rerun proceeds. An answer of 204 (<c>AlreadyAtTarget</c>) would read as a rejection: the
/// replacement would exit 1 and every drained run would end there (<c>docs/internals/decisions.md</c>, "Agent Jobs").
/// What keeps the answer at 200 is the idempotent check in <c>TryRecoverFromInfrastructureFailureCoreAsync</c>.
/// </para>
///
/// <para>
/// The first test pins the wire answer and the client's reading of it. The second runs two real workers with the same
/// agent id, as the Job gives a replacement pod, and pins that the rerun succeeds.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RetriedPodTests : HeadlessE2ETestBase
{
    public RetriedPodTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would.
    /// Returns the claimed work item GUID.
    ///
    /// <para>
    /// The real agent authenticates via <c>DeriveKey(masterApiKey, agentId)</c>. The API
    /// authorises <c>GET /api/work-items/{id}/assignment</c> and <c>POST /api/work-items/{id}/status</c>
    /// by checking that the bearer token matches <c>DeriveKey(masterApiKey, AssignedAgentId)</c>.
    /// Pre-claiming the work item with <paramref name="agentId"/> as <c>AssignedAgentId</c> satisfies that check.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e");

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = WorkItemTaskType.Implementation;

        // Build the identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment
        // enriches it with configs. LocalPipelineExecutor's ProviderFactoryOverride still bypasses the
        // provider config lookup because fakeProviders is injected.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-retried-pod-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Implementation,
            IssueDetail = Fixture.IssueProvider.Issues.FirstOrDefault(i => i.Identifier == issueId)
                ?? new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the "e2e" selector.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-agent",
            DisplayName = "Real agent smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(
            minimalPayload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        await db.SaveChangesAsync(ct);

        // Spec 056: POST /dispatch routes the item and pins its route; the claim refuses an item dispatched without one.
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, ct);
        Assert.Equal(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed, route.Status);

        // Marked Dispatched to the real agent, as the dispatch path would.
        await using var dispatchDb = Fixture.DbContextFactory.CreateDbContext();
        var dispatched = await dispatchDb.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
        dispatched.Status = WorkItemStatus.Dispatched;
        dispatched.AssignedAgentId = agentId;
        dispatched.DispatchedAt = DateTimeOffset.UtcNow;
        await dispatchDb.SaveChangesAsync(ct);

        return workItemId;
    }

    private async Task<WorkItemEntity> ReadWorkItemAsync(Guid workItemId)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemId);
    }

    // ── Running reported twice by the same agent ─────────────────────────────────────

    /// <summary>
    /// An agent reports <c>Running</c> on a work item that is already <c>Running</c>, as a replacement pod does after a
    /// drain. The API answers 200 and the client reads it as accepted; a 204 would read as a rejection and the agent
    /// would exit 1.
    ///
    /// <para>
    /// The first report is a raw POST so the status code is visible, the second goes through the real
    /// <see cref="WorkItemHttpClient"/> so its reading of the answer is pinned too. Both authenticate as the assigned
    /// agent, not with the master key, so the API's check that the caller owns the work item runs.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenRunningPostedTwiceByTheSameAgent_ShouldAnswer200AndClientShouldAccept()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        var agentId = $"retried-pod-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync("7901", agentId);

        // The claim leaves the item Dispatched; the first pod's Running report is what the item already holds.
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var row = await db.WorkItems.SingleAsync(w => w.Id == workItemId);
            row.Status = WorkItemStatus.Running;
            await db.SaveChangesAsync();
        }

        using var http = new HttpClient { BaseAddress = new Uri(Fixture.AgentHubUrl) };
        http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", AgentKeyDerivation.DeriveAgentKey(Fixture.ApiKey, agentId));
        var update = new WorkItemStatusUpdate { Status = "Running", AgentId = agentId };

        // ── Act 1: the wire answer ─────────────────────────────────────────────────

        using var content = JsonContent.Create(update, options: PipelineJsonOptions.Default);
        using var response = await http.PostAsync(
            $"/api/work-items/{workItemId}/status?agentId={Uri.EscapeDataString(agentId)}", content);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a Running report on a Running item is Transitioned (200); AlreadyAtTarget (204) reads as a rejection");

        // ── Act 2: the client's reading of it ──────────────────────────────────────

        var client = new WorkItemHttpClient(http, Serilog.Log.Logger) { AgentId = agentId };
        var accepted = await client.PostStatusAsync(workItemId.ToString(), update, CancellationToken.None);

        // ── Assert ─────────────────────────────────────────────────────────────────

        accepted.Should().BeTrue("the client accepts a 200, so the agent carries on instead of exiting 1");

        var item = await ReadWorkItemAsync(workItemId);
        item.Status.Should().Be(WorkItemStatus.Running, "the repeated report changes nothing");
        item.CompletedAt.Should().BeNull("a Running report must not complete the item");
    }

    // ── Drained pod, replaced ────────────────────────────────────────────────────────

    /// <summary>
    /// A pod is drained in the middle of its run, and Kubernetes starts a replacement with the same agent id. The first
    /// worker stops on SIGTERM inside its first agent call: it exits 143 and posts nothing, so the work item stays
    /// <c>Running</c>. The second worker reports <c>Running</c> again, runs the pipeline from the start and finishes the
    /// item.
    ///
    /// <para>
    /// The test stops the first worker by disposing its harness, which stops the host, as SIGTERM does to the pod. The
    /// first worker's scripted provider holds its call until the worker's token is cancelled, so no script is used up and
    /// the second worker gets all three.
    /// </para>
    ///
    /// <para>
    /// The agent calls after code generation (pre-PR cleanup, PR description, feedback) are not scripted: the provider
    /// answers them with exit 0 and no output, which the pipeline tolerates. For that reason, and because the first
    /// worker's call is recorded as unscripted, the test does not assert that no call was unscripted.
    /// </para>
    ///
    /// <para>
    /// Both workers use the one workspace root the harness gives all workers, so the test does not rely on it being
    /// empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenDrainedPodIsReplaced_RerunShouldProceedAndSucceed()
    {
        const string issueId = "7001";

        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] All methods validate inputs",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "retried-pod-template",
            Name = "Retried Pod Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "retried-pod-profile",
            DisplayName = "Retried Pod Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        Fixture.QualityGateValidator.AlwaysPass();

        // Both workers set this process-global value when they stop.
        var savedExitCode = Environment.ExitCode;

        // Declared with a plain var and disposed by hand: the harness disposes its host again if asked twice.
        var firstPod = new RealAgentWorkerHarness();
        var firstPodDisposed = false;
        try
        {
            // ── Act: the first pod runs into the drain ─────────────────────────────

            // No scripts yet: the first provider call blocks until the worker's token is cancelled.
            Fixture.AgentProvider.PauseBeforeExecution = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // A replacement pod gets the Job's name as its agent id, so both pods share one.
            var agentId = $"retried-pod-{Guid.NewGuid():N}";
            var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

            await firstPod.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await WaitUntilAsync(() => Fixture.AgentProvider.Calls.Count >= 1, TimeSpan.FromSeconds(20));

            // Stopping the host is what SIGTERM does to the pod.
            await firstPod.DisposeAsync();
            firstPodDisposed = true;

            // ── Assert: the first pod recorded no outcome ──────────────────────────

            var afterDrain = await ReadWorkItemAsync(workItemId);
            afterDrain.Status.Should().Be(WorkItemStatus.Running, "a drained pod posts no status, so the item did not go terminal");
            Environment.ExitCode.Should().Be(143, "a worker that SIGTERM stopped exits non-zero, so the Job replaces the pod");

            // ── Act: the replacement pod starts the run again ──────────────────────

            Fixture.AgentProvider.PauseBeforeExecution = null;
            Fixture.AgentProvider
                .EnqueueReadyAnalysis()  // analysis agent
                .EnqueueReadyAnalysis()  // adversarial review agent
                .EnqueueCodeGen();       // code generation agent

            await using var secondPod = new RealAgentWorkerHarness();
            await secondPod.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await secondPod.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(20));

            // ── Assert: the rerun succeeded ────────────────────────────────────────

            var finalItem = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
            finalItem.CompletedAt.Should().NotBeNull();

            Environment.ExitCode.Should().Be(0,
                "the replacement pod's Running report is answered with 200, so it runs the pipeline and records the outcome");

            Fixture.IssueProvider.Issues.Single(i => i.Identifier == issueId)
                .Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal))
                .Should().Equal(AgentLabels.Done);
        }
        finally
        {
            try
            {
                if (!firstPodDisposed)
                    await firstPod.DisposeAsync();
            }
            finally
            {
                Environment.ExitCode = savedExitCode;
            }
        }
    }
}
