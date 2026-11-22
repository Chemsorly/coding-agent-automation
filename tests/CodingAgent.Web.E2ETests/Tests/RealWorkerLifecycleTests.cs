using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Multi-step lifecycles on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API
/// host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>. Each stage of these chains is
/// tested somewhere else; here the whole chain runs, and a later stage starts from what the earlier stage left behind.
///
/// <para>
/// The fixture starts no real worker for an item the loop dispatched. So the real workers run for items the test claims
/// itself, as in <see cref="RealWorkerDecompositionTests"/>, and a <see cref="FakeAgentClient"/> proves that the loop
/// dispatches what a worker created. A person's actions on the tracker (the approval label, an edited issue body) are
/// made directly on <see cref="InMemoryIssueProvider"/>.
/// </para>
///
/// <para>
/// The re-analysis in the requeue test is forced by the gate-rejection comment that the first run leaves, which is newer
/// than its analysis comment. The edited body does not decide it, so the test does not cover the <c>body_changed</c>
/// signal: <c>NeedsRefinementRequeueTests.BodyChanged_ForcesFreshAnalysis</c> does.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerLifecycleTests : HeadlessE2ETestBase
{
    /// <summary>The project of the work items: the one the templates of <see cref="SeedTemplateAndProfileAsync"/> are saved in.</summary>
    private static readonly Guid DefaultProject = Guid.Parse(WellKnownIds.DefaultProjectId);

    public RealWorkerLifecycleTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private static string NewAgentId() => $"lifecycle-{Guid.NewGuid():N}";

    /// <summary>
    /// Saves the Default project's template for the E2E tracker and repository, and a routed profile for the <c>e2e</c>
    /// job template. Without an enabled template for the tracker the enricher refuses the assignment and the worker
    /// retries until the test times out.
    /// </summary>
    private async Task SeedTemplateAndProfileAsync(bool decompositionEnabled = false)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-lifecycle-template",
            Name = "Real Lifecycle Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = decompositionEnabled
        }, CancellationToken.None);

        await SeedRoutedProfileAsync();
    }

    private Task SeedRoutedProfileAsync() =>
        Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-lifecycle",
            DisplayName = "Real lifecycle profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

    /// <summary>
    /// Creates a work item of <paramref name="taskType"/> for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. Returns the work item GUID.
    ///
    /// <para>
    /// A copy of <c>RealWorkerDecompositionTests.CreateAndClaimWorkItemAsync</c> with the task type as a parameter, for
    /// the decomposition phases and for implementation runs. The row and the payload must agree on the project and the
    /// tracker. The payload's issue is the one <see cref="InMemoryIssueProvider"/> holds now, so a change to the issue
    /// has to be made before the claim.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        WorkItemTaskType taskType,
        PipelineRunType runType)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e", projectId: DefaultProject);

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = taskType;

        var payload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-lifecycle-test",
            TaskType = taskType,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = runType,
            ProjectId = DefaultProject,
            IssueDetail = Fixture.IssueProvider.Issues.FirstOrDefault(i => i.Identifier == issueId)
                ?? new IssueDetail { IsAuthorVerified = true, Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the "e2e" selector.
        await SeedRoutedProfileAsync();
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);

        await db.SaveChangesAsync();

        await RouteAsync(workItemId);

        await MarkDispatchedAsync(workItemId, agentId);

        return workItemId;
    }

    /// <summary>
    /// Claims the Pending item that a re-dispatch created for <paramref name="agentId"/>: routes it, then marks it
    /// Dispatched. The payload the dispatcher wrote stays as it is, except for the route that routing pins on it.
    ///
    /// <para>
    /// The item must be routed first. A work item is created without a route, and the claim refuses an item that was
    /// dispatched without one. The production dispatcher routes it, and <see cref="FakeJobController"/> does only when an
    /// idle agent with matching labels is connected, which these tests do not do.
    /// </para>
    /// </summary>
    private async Task ClaimExistingWorkItemAsync(Guid id, string agentId)
    {
        await RouteAsync(id);

        await MarkDispatchedAsync(id, agentId);
    }

    /// <summary>
    /// Spec 056: POST /dispatch routes the item and pins its route; the claim refuses an item dispatched without one.
    /// </summary>
    private async Task RouteAsync(Guid workItemId)
    {
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, CancellationToken.None);
        route.Status.Should().Be(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed, "the work item must carry a pinned route to be claimed");
    }

    /// <summary>Marks the work item Dispatched to <paramref name="agentId"/>, as the dispatch path would.</summary>
    private async Task MarkDispatchedAsync(Guid workItemId, string agentId)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], CancellationToken.None)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
        entity.Status = WorkItemStatus.Dispatched;
        entity.AssignedAgentId = agentId;
        entity.DispatchedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }

    /// <summary>Runs the real worker for <paramref name="workItemId"/> until it stops.</summary>
    private async Task RunWorkerAsync(string agentId, Guid workItemId)
    {
        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Waits until the labels of <paramref name="issueId"/> that start with <c>agent:</c> are exactly
    /// <paramref name="expected"/>. The terminal label is set through the hub and again when the API records the
    /// completion, so it can land after the worker has stopped. The final assertion names the labels that were found.
    /// </summary>
    private async Task AssertAgentLabelsAsync(string issueId, params string[] expected)
    {
        async Task<List<string>> AgentLabelsAsync()
        {
            // The API rewrites the fake's issue list from another thread, so a read that overlaps a write throws
            // "Collection was modified". The write is a single indexer set, so a plain re-read succeeds.
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
                    return issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();
                }
                catch (InvalidOperationException) when (attempt < 5)
                {
                    // Read again.
                }
            }
        }

        try
        {
            await WaitUntilAsync(
                async () => (await AgentLabelsAsync()).Order().SequenceEqual(expected.Order()),
                TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The assertion below reports the labels the issue ended with.
        }

        (await AgentLabelsAsync()).Should().BeEquivalentTo(expected, "the agent labels of issue {0}", issueId);
    }

    /// <summary>
    /// Waits for a work item of <paramref name="issueId"/> other than <paramref name="excludedId"/> and returns its GUID.
    ///
    /// <para>
    /// <see cref="HeadlessE2ETestBase.WaitForWorkItemAsync"/> takes a delegate, which EF Core cannot translate on the
    /// in-memory provider (the reason <see cref="HeadlessE2ETestBase.WaitForWorkItemByIdUntilTerminalAsync"/> exists), so
    /// the same condition is queried as an expression.
    /// </para>
    /// </summary>
    private async Task<Guid> WaitForNewWorkItemAsync(string issueId, Guid excludedId)
    {
        var found = Guid.Empty;
        await WaitUntilAsync(
            async () =>
            {
                await using var db = Fixture.DbContextFactory.CreateDbContext();
                var item = await db.WorkItems.AsNoTracking()
                    .FirstOrDefaultAsync(w => w.IssueIdentifier == issueId && w.Id != excludedId);
                if (item is null)
                    return false;
                found = item.Id;
                return true;
            },
            TimeSpan.FromSeconds(10));
        return found;
    }

    /// <summary>The status of the work item <paramref name="workItemId"/>, read from the database.</summary>
    private async Task<WorkItemStatus> WorkItemStatusAsync(Guid workItemId)
    {
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        return (await db.WorkItems.AsNoTracking().FirstAsync(w => w.Id == workItemId)).Status;
    }

    // ── D2: epic to child work ──────────────────────────────────────────────────────

    /// <summary>
    /// The whole chain of an epic: Phase 1 posts the plan and sets <c>agent:epic-review</c>, a person approves on the
    /// tracker, Phase 2 creates two sub-issues and sets <c>agent:done</c>, and the loop then picks up one of the children
    /// and hands it to an agent as an implementation run.
    ///
    /// <para>
    /// The loop is started only after both workers have finished: a loop that runs earlier sees the epic and claims it for
    /// the fake agent. The children have no dependency on each other on purpose, because a dependent child is blocked until
    /// its parent is closed.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenEpicIsPlannedApprovedAndCreated_LoopShouldPickUpAChild()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const string epicId = "7701";

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = epicId,
            Title = "Epic: Implement feature X",
            Description = "## Goal\nBuild feature X end-to-end",
            Labels = [AgentLabels.Epic]
        });
        Fixture.IssueProvider.ListCreatedIssues = true;
        await SeedTemplateAndProfileAsync(decompositionEnabled: true);
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { ClosedLoopPollInterval = TimeSpan.FromSeconds(1) }, CancellationToken.None);

        // ── Act and assert: Phase 1 ────────────────────────────────────────────────

        // The plan agent and the adversarial review of the plan.
        Fixture.AgentProvider
            .EnqueueDecompositionPlan()
            .EnqueueDecompositionPlan();

        var phase1AgentId = NewAgentId();
        var phase1WorkItemId = await CreateAndClaimWorkItemAsync(
            epicId, phase1AgentId, WorkItemTaskType.Decomposition, PipelineRunType.DecompositionAnalysis);

        await RunWorkerAsync(phase1AgentId, phase1WorkItemId);

        Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty(
            "every agent call in Phase 1 must be scripted; unscripted prompts: {0}",
            string.Join(" | ", Fixture.AgentProvider.UnscriptedCalls.Select(c => c.Request.Prompt.Split('\n')[0])));

        await AssertAgentLabelsAsync(epicId, AgentLabels.EpicReview);
        Fixture.IssueProvider.PostedComments.ToList()
            .Where(c => c.Identifier == epicId && c.Body.Contains(CommentMarkers.DecompositionPlan))
            .Should().ContainSingle("Phase 1 posts the plan on the epic");

        // ── Act and assert: the approval, as a person does it on the tracker ───────

        await Fixture.IssueProvider.RemoveLabelAsync(epicId, AgentLabels.EpicReview, CancellationToken.None);
        await Fixture.IssueProvider.AddLabelsAsync(epicId, [AgentLabels.EpicApproved], CancellationToken.None);

        // ── Act and assert: Phase 2 ────────────────────────────────────────────────

        Fixture.AgentProvider.EnqueueSubIssues(
            new SubIssueFile("First child", "## Requirements\nDo one."),
            new SubIssueFile("Second child", "## Requirements\nDo two."));

        var phase2AgentId = NewAgentId();
        var phase2WorkItemId = await CreateAndClaimWorkItemAsync(
            epicId, phase2AgentId, WorkItemTaskType.Decomposition, PipelineRunType.Decomposition);

        await RunWorkerAsync(phase2AgentId, phase2WorkItemId);

        Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty(
            "every agent call in Phase 2 must be scripted; unscripted prompts: {0}",
            string.Join(" | ", Fixture.AgentProvider.UnscriptedCalls.Select(c => c.Request.Prompt.Split('\n')[0])));

        await AssertAgentLabelsAsync(epicId, AgentLabels.Done);
        Fixture.IssueProvider.CreatedIssues.ToList().Should().HaveCount(2);

        // ── Act and assert: the loop picks up a child ──────────────────────────────

        await using var fakeAgent = new FakeAgentClient("child-agent", "e2e");
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        try
        {
            (await Fixture.StartLoopAsync()).Should().BeTrue("the loop must start with the enabled template");

            var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

            assignment.RunType.Should().Be(PipelineRunType.Implementation);
            // The snapshot is a copy: the hub thread swaps the labels of the child it just dispatched.
            Fixture.IssueProvider.Issues.ToArray()
                .Single(i => i.Identifier == assignment.IssueIdentifier).Title
                .Should().BeOneOf("First child", "Second child");
        }
        finally
        {
            await Fixture.StopLoopAsync();
        }
    }

    // ── L1: failed run, then re-dispatch ────────────────────────────────────────────

    /// <summary>
    /// A run whose quality gates never pass ends Failed with a draft pull request and <c>agent:error</c>. The Run page's
    /// re-dispatch queues a new work item for the same issue, and a real worker that claims it succeeds. History keeps both
    /// runs, and the issue ends <c>agent:done</c>.
    ///
    /// <para>
    /// No <see cref="FakeAgentClient"/> is connected, so <see cref="FakeJobController"/> leaves the re-dispatched item
    /// Pending until the test claims it. The second run finds the analysis comment of the first and reuses it: the first
    /// run did not fail with an agent error, the body is unchanged and no gate rejected it. So the analysis scripts of the
    /// second run are taken by later agent calls, which all exit 0, and the run still succeeds.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenFailedRunIsRedispatched_RealWorkerShouldSucceedAndHistoryKeepBothRuns()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const string issueId = "7702";

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] All methods validate inputs",
            Labels = [AgentLabels.Next]
        });
        await SeedTemplateAndProfileAsync();
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(c => c with { MaxRetries = 1 }, CancellationToken.None);

        // ── Act and assert: the first run fails ────────────────────────────────────

        // Analysis, adversarial review, code generation. The fix agent and the failure feedback run on the empty queue.
        Fixture.QualityGateValidator.AlwaysFail();
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var firstAgentId = NewAgentId();
        var firstId = await CreateAndClaimWorkItemAsync(
            issueId, firstAgentId, WorkItemTaskType.Implementation, PipelineRunType.Implementation);

        await RunWorkerAsync(firstAgentId, firstId);

        await WaitForWorkItemStatusAsync(firstId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);

        var failed = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Failed);

        // What CanRedispatch needs of the run: a worker run is rebuilt from the work item, so these come from its payload.
        failed.RunType.Should().Be(PipelineRunType.Implementation);
        failed.IssueProviderConfigId.Should().NotBeNullOrEmpty();
        failed.RepoProviderConfigId.Should().NotBeNullOrEmpty();

        // ── Act and assert: the re-dispatch ────────────────────────────────────────

        using var api = Fixture.CreateUiApiClient();
        var response = await api.PostAsync($"/api/ui/runs/{failed.RunId}/redispatch", null);
        var responseBody = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue(
            "the failed run must be re-dispatchable, but the API answered {0}: {1}", (int)response.StatusCode, responseBody);

        var secondId = await WaitForNewWorkItemAsync(issueId, firstId);

        // ── Act and assert: the second run succeeds ────────────────────────────────

        Fixture.QualityGateValidator.AlwaysPass();
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var callsBeforeSecondRun = Fixture.AgentProvider.Calls.Count;
        var repoCallsBeforeSecondRun = Fixture.RepositoryProvider.MethodCalls.ToList().Count;

        var secondAgentId = NewAgentId();
        await ClaimExistingWorkItemAsync(secondId, secondAgentId);

        await RunWorkerAsync(secondAgentId, secondId);

        await WaitForWorkItemStatusAsync(secondId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        (await WorkItemStatusAsync(firstId)).Should().Be(WorkItemStatus.Failed, "the first run's row is not reused");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);

        // The scripts above are not asserted one by one (see the summary above), so show that the second run did the work:
        // the worker called the agent, and it pushed its branch and opened a pull request of its own.
        Fixture.AgentProvider.Calls.Skip(callsBeforeSecondRun)
            .Should().NotBeEmpty("the second run's real worker called the agent");
        Fixture.RepositoryProvider.MethodCalls.ToList().Skip(repoCallsBeforeSecondRun)
            .Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
                "the second run created its own pull request, apart from the draft the failed run left");

        var completed = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed);
        completed.RunId.Should().NotBe(failed.RunId);

        var history = await Fixture.HistoryService.GetRunHistoryAsync();
        history.Should().Contain(r => r.RunId == failed.RunId && r.FinalStep == PipelineStep.Failed,
            "the failed run stays in the history next to the one that succeeded");
        history.Should().Contain(r => r.RunId == completed.RunId);
    }

    // ── L2: needs refinement, then requeue ──────────────────────────────────────────

    /// <summary>
    /// An analysis that says <c>not_ready</c> sends the issue to <c>agent:needs-refinement</c>. A person edits the body and
    /// sets <c>agent:next</c> again, and a real worker that claims a new work item analyses again and finishes with a pull
    /// request and <c>agent:done</c>. The analysis comment of the second run carries text that only the second run's
    /// analysis agent wrote.
    ///
    /// <para>
    /// The second run updates the analysis comment of the first run, so its text is in the updated comments, not in the
    /// posted ones.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenRejectedIssueIsEditedAndRequeued_RealWorkerShouldAnalyseAgainAndFinish()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        const string issueId = "7703";
        const string title = "Add input validation";

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nToo vague.",
            Labels = [AgentLabels.Next]
        });
        await SeedTemplateAndProfileAsync();
        Fixture.QualityGateValidator.AlwaysPass();

        // ── Act and assert: the first run is rejected ──────────────────────────────

        // The analysis agent and the adversarial review both say not_ready.
        Fixture.AgentProvider
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria")
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria");

        var firstAgentId = NewAgentId();
        var firstId = await CreateAndClaimWorkItemAsync(
            issueId, firstAgentId, WorkItemTaskType.Implementation, PipelineRunType.Implementation);

        await RunWorkerAsync(firstAgentId, firstId);

        await WaitForWorkItemStatusAsync(firstId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));
        await AssertAgentLabelsAsync(issueId, AgentLabels.NeedsRefinement);

        // ── Act: a person edits the body and requeues ──────────────────────────────

        // IssueDetail is immutable, so the issue is replaced. The claim below takes the new one into its payload.
        Fixture.IssueProvider.Issues.RemoveAll(i => i.Identifier == issueId);
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] Methods validate input",
            Labels = [AgentLabels.Next]
        });

        // ── Act and assert: the second run analyses again and finishes ─────────────

        // Analysis, adversarial review, code generation. Only the first writes the marker, and the review that follows it
        // does not trigger a refinement, so the analysis comment keeps the marker.
        const string fresh = "FRESH-ANALYSIS-MARKER";
        Fixture.AgentProvider
            .Enqueue(new AnalysisScript { Recommendation = "ready", AnalysisContent = fresh + new string('x', 200) })
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var callsBeforeSecondRun = Fixture.AgentProvider.Calls.Count;

        var secondAgentId = NewAgentId();
        var secondId = await CreateAndClaimWorkItemAsync(
            issueId, secondAgentId, WorkItemTaskType.Implementation, PipelineRunType.Implementation);

        await RunWorkerAsync(secondAgentId, secondId);

        await WaitForWorkItemStatusAsync(secondId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);

        // The edit reached the second run: the acceptance criterion only the edited body has is in the prompts of the
        // second run's agents, and in none of the first run's.
        const string editedCriterion = "Methods validate input";
        var prompts = Fixture.AgentProvider.Calls.Select(c => c.Request.Prompt).ToList();
        prompts.Take(callsBeforeSecondRun).Should().NotContain(p => p.Contains(editedCriterion),
            "the first run analysed the body as it was before the edit");
        prompts.Skip(callsBeforeSecondRun).Should().Contain(p => p.Contains(editedCriterion),
            "the second run's agents were given the edited body");

        var posted = Fixture.IssueProvider.PostedComments.ToList()
            .Where(c => c.Identifier == issueId).Select(c => c.Body);
        var updated = Fixture.IssueProvider.UpdatedComments.ToList()
            .Where(c => c.Identifier == issueId).Select(c => c.NewBody);
        posted.Concat(updated).Should().Contain(body => body.Contains(fresh),
            "that text exists only if the second run's analysis agent ran");

        Fixture.RepositoryProvider.MethodCalls.ToList().Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the first run ended at the analysis gate, so only the second run can have created the pull request");
    }
}
