using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// How the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> ends when its run is interrupted, in-process against
/// the E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// A <see cref="FakeAgentClient"/> ignores a cancel and has no reconnect logic, so nothing else shows a real worker
/// stopping on a cancel or surviving a dropped hub connection. Three interruptions, all in the middle of code generation:
/// an operator cancel, SIGTERM alone, and a hub connection that drops and comes back.
/// </para>
///
/// <para>
/// Each test parks the worker inside the code generation call. The adversarial review script sets
/// <see cref="ScriptedAgentProvider.PauseBeforeExecution"/>, so the next call, code generation, waits there and honours
/// its cancellation token.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerInterruptionTests : HeadlessE2ETestBase
{
    public RealWorkerInterruptionTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would.
    /// Returns the claimed work item GUID.
    ///
    /// <para>
    /// The real agent authenticates via <c>DeriveKey(masterApiKey, agentId)</c>. The API
    /// authorises <c>GET /api/work-items/{id}/assignment</c> by checking that the bearer token
    /// matches <c>DeriveKey(masterApiKey, AssignedAgentId)</c>. Pre-claiming the work item with
    /// <paramref name="agentId"/> as <c>AssignedAgentId</c> satisfies that check.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e");

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = taskType;

        // Build the identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment
        // enriches it with configs. LocalPipelineExecutor's ProviderFactoryOverride still bypasses the
        // provider config lookup because fakeProviders is injected.
        var runType = taskType == WorkItemTaskType.Decomposition
            ? PipelineRunType.DecompositionAnalysis
            : PipelineRunType.Implementation;
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-smoke-test",
            TaskType = taskType,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = runType,
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

    private IReadOnlyList<string> AgentLabelsOn(string id) =>
        Fixture.IssueProvider.Issues.Single(i => i.Identifier == id)
            .Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();

    private async Task<WorkItemEntity> ReadWorkItemAsync(Guid workItemId)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == workItemId);
    }

    /// <summary>Seeds an <c>agent:next</c> issue with the template its worker needs to be assigned.</summary>
    private async Task SeedIssueAsync(string issueId, string templateId)
    {
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
            Id = templateId,
            Name = templateId,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = $"profile-{templateId}",
            DisplayName = $"Profile {templateId}",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });
    }

    /// <summary>
    /// Seeds the issue, scripts the run so that code generation waits on <paramref name="pause"/>, claims the work item
    /// for a fresh agent id and starts <paramref name="harness"/> against <paramref name="agentHubUrl"/>. Returns once the
    /// worker is inside the code generation call.
    ///
    /// <para>
    /// The scripts, in the order the pipeline calls the agent: the analysis, then the adversarial review, which only sets
    /// the pause, then code generation, which finds it set. The review writes no files, so the pipeline keeps the analysis
    /// from the first call.
    /// </para>
    ///
    /// <para>
    /// The run reports <c>GeneratingCode</c> just before the agent call, and the pause was set by then. The report is sent
    /// without waiting, so the helper also waits for the call itself: a test that then cancels the worker can rely on
    /// there being exactly three calls.
    /// </para>
    /// </summary>
    private async Task<(Guid WorkItemId, string AgentId)> StartPausedRunAsync(
        string issueId,
        string templateId,
        RealAgentWorkerHarness harness,
        TaskCompletionSource pause,
        string? agentHubUrl = null)
    {
        await SeedIssueAsync(issueId, templateId);

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()                                     // analysis agent
            .Enqueue(new SetPauseScript(Fixture.AgentProvider, pause))  // adversarial review agent
            .EnqueueCodeGen();                                          // code generation agent, paused

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await harness.StartAsync(
            agentHubUrl: agentHubUrl ?? Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await WaitUntilAsync(() =>
            Fixture.RunService.GetRun(new RunId(workItemId.ToString()))?.CurrentStep == PipelineStep.GeneratingCode
            && Fixture.AgentProvider.Calls.Count == 3);

        return (workItemId, agentId);
    }

    /// <summary>Stands in for the review call: the next agent call, code generation, waits on <paramref name="pause"/>.</summary>
    private sealed class SetPauseScript(ScriptedAgentProvider provider, TaskCompletionSource pause) : AgentScript
    {
        public override void Execute(string workspacePath) => provider.PauseBeforeExecution = pause;
    }

    // ── Operator cancel ─────────────────────────────────────────────────────────────

    /// <summary>
    /// An operator cancels the run during code generation. The API moves the work item to Cancelled, swaps the label to
    /// <c>agent:cancelled</c> and writes the history row, then Kubernetes deletes the Job and sends SIGTERM to the pod.
    /// The test plays Kubernetes by stopping the worker's host.
    ///
    /// <para>
    /// The cancel goes through <c>IWorkDistributor.CancelJobAsync</c>, which reaches the same transition service as the
    /// Run page's cancel endpoint. Nothing sends the worker a hub cancel in Kubernetes mode, so it keeps running until
    /// the SIGTERM.
    /// </para>
    ///
    /// <para>
    /// The run ends Cancelled with no pull request and no <c>agent:done</c>. The worker's exit code is not asserted here:
    /// <see cref="WhenSigtermHitsMidRun_ShouldPostNoTerminalStatusAndExit143"/> covers it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenOperatorCancelsDuringCodegen_WorkerShouldStopWithCancelledLabelAndNoPullRequest()
    {
        const string issueId = "6401";

        // The worker sets this process-global value when it stops.
        var exitCode = Environment.ExitCode;
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RealAgentWorkerHarness();
        try
        {
            var (workItemId, _) = await StartPausedRunAsync(issueId, "real-interrupt-cancel-template", harness, pause);

            var cancelled = await Fixture.ApiServices.GetRequiredService<IWorkDistributor>()
                .CancelJobAsync(new JobId(workItemId.ToString()), CancellationToken.None);
            cancelled.Should().BeTrue("a Running work item can be cancelled");

            await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled);

            harness.SimulateSigterm();
            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            var entity = await ReadWorkItemAsync(workItemId);
            entity.Status.Should().Be(WorkItemStatus.Cancelled, "the worker's stop must not change a cancelled work item");

            AgentLabelsOn(issueId).Should().Equal(AgentLabels.Cancelled);
            Fixture.IssueProvider.LabelChanges.Should().NotContain(c =>
                c.Identifier == issueId && c.Label == AgentLabels.Done && c.Added,
                "a cancelled run is never done");

            Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
                "the run stopped in code generation");

            // The cancel writes the history row, and its FinalStep is the run's step at that moment.
            await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Cancelled);

            Fixture.AgentProvider.Calls.Should().HaveCount(3,
                "analysis, adversarial review and the paused code generation; nothing ran after the cancel");
        }
        finally
        {
            // Release the pause and stop the worker before restoring: stopping it sets the exit code again.
            pause.TrySetResult();
            try
            {
                await harness.DisposeAsync();
            }
            finally
            {
                Environment.ExitCode = exitCode;
            }
        }
    }

    // ── SIGTERM ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// SIGTERM during code generation, with no cancel before it, as a node drain delivers it. The worker stops, posts no
    /// terminal status and exits 143, so the Kubernetes Job retries the pod (<c>docs/internals/decisions.md</c>, "Agent
    /// Jobs"): the outcome stays open. This pins that on the real pipeline.
    ///
    /// <para>
    /// The assertions follow straight after the worker has stopped. A later heartbeat timeout may fail the run, and that
    /// is not part of this test.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenSigtermHitsMidRun_ShouldPostNoTerminalStatusAndExit143()
    {
        const string issueId = "6403";

        var exitCode = Environment.ExitCode;
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RealAgentWorkerHarness();
        try
        {
            var (workItemId, _) = await StartPausedRunAsync(issueId, "real-interrupt-sigterm-template", harness, pause);

            harness.SimulateSigterm();
            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            Environment.ExitCode.Should().Be(143, "a worker that SIGTERM stopped exits non-zero, so the Job retries it");

            // Only a status POST writes Result, and only a terminal one changes the status away from Running.
            var entity = await ReadWorkItemAsync(workItemId);
            entity.Status.Should().NotBe(WorkItemStatus.Cancelled, "SIGTERM is not reported as a cancel");
            entity.Status.Should().NotBe(WorkItemStatus.Succeeded);
            entity.Result.Should().BeNull("the worker posts no terminal status for SIGTERM");

            var labels = AgentLabelsOn(issueId);
            labels.Should().NotContain(AgentLabels.Cancelled);
            labels.Should().NotContain(AgentLabels.Done);
            labels.Should().NotContain(AgentLabels.Error);

            Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
                "the run stopped in code generation");
        }
        finally
        {
            pause.TrySetResult();
            try
            {
                await harness.DisposeAsync();
            }
            finally
            {
                Environment.ExitCode = exitCode;
            }
        }
    }

    // ── Transient hub drop ──────────────────────────────────────────────────────────

    /// <summary>
    /// The hub connection breaks during code generation and comes back. The worker's SignalR client reconnects on its
    /// own and registers again with its active job, so the API links the new connection to the same run. The run then
    /// finishes with exactly one terminal label, <c>agent:done</c>.
    ///
    /// <para>
    /// All the worker's traffic goes through a <see cref="TcpDropProxy"/>, which resets every open socket on both sides.
    /// That is a broken transport, so the client's automatic reconnect runs; stopping the connection would take the
    /// terminal path instead, which builds a new connection while the pipeline keeps the old one. The proxy keeps
    /// listening, so the reconnect and the worker's later HTTP calls get new connections.
    /// </para>
    ///
    /// <para>
    /// The drop happens while the worker is paused, with no request in flight. The pause is released only once the
    /// registry holds a new connection id for the agent and shows it connected: a hub report sent while the client is still
    /// reconnecting would be lost. The history row with <see cref="PipelineStep.Completed"/> is written only by the hub
    /// report, so it proves that the report landed on the new connection.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenHubDropsBrieflyDuringCodegen_RunShouldFinishWithOneTerminalLabel()
    {
        const string issueId = "6402";

        var exitCode = Environment.ExitCode;
        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // Declared before the harness, so it is disposed after it: the worker's client retries until it stops.
        await using var proxy = new TcpDropProxy(new Uri(Fixture.AgentHubUrl));
        var harness = new RealAgentWorkerHarness();
        try
        {
            Fixture.QualityGateValidator.AlwaysPass();
            var (workItemId, agentId) = await StartPausedRunAsync(
                issueId, "real-interrupt-hub-drop-template", harness, pause, agentHubUrl: proxy.Url);

            var before = Fixture.AgentRegistry.GetByAgentId(new AgentId(agentId))!.ConnectionId;
            var connectionsBefore = proxy.ConnectionCount;

            proxy.DropAllConnections();

            // Only RegisterAgent changes the connection id, so this holds once the worker has reconnected and registered.
            await WaitUntilAsync(
                () => Fixture.AgentRegistry.GetByAgentId(new AgentId(agentId)) is { } entry
                    && entry.ConnectionId != before
                    && entry.Status != AgentStatus.Disconnected,
                TimeSpan.FromSeconds(30));

            pause.TrySetResult();
            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

            // The hub report writes the history row ahead of the label swap; the worker has already returned, so both
            // are done.
            await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed);

            AgentLabelsOn(issueId).Should().Equal(AgentLabels.Done);

            var terminalLabels = new HashSet<string>
            {
                AgentLabels.Done, AgentLabels.Error, AgentLabels.Cancelled, AgentLabels.NeedsRefinement, AgentLabels.WontDo
            };
            // Distinct on purpose: the completion path itself adds agent:done twice in every run, drop or not, so the
            // number of adds cannot show a duplicated report. The history row and pull request counts below do.
            Fixture.IssueProvider.LabelChanges
                .Where(c => c.Identifier == issueId && c.Added && terminalLabels.Contains(c.Label))
                .Select(c => c.Label)
                .Distinct()
                .Should().Equal(AgentLabels.Done);

            (await Fixture.HistoryService.GetRunHistoryAsync())
                .Count(r => r.IssueIdentifier == issueId)
                .Should().Be(1, "the terminal report reached the API once, not again after the reconnect");

            Fixture.RepositoryProvider.MethodCalls
                .Count(m => m == nameof(IRepositoryProvider.CreatePullRequestAsync))
                .Should().Be(1, "the run finished its pipeline once, after the reconnect");

            proxy.ConnectionCount.Should().BeGreaterThan(connectionsBefore,
                "the worker opened new connections through the proxy after the drop");
        }
        finally
        {
            pause.TrySetResult();
            try
            {
                await harness.DisposeAsync();
            }
            finally
            {
                Environment.ExitCode = exitCode;
            }
        }
    }
}
