using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Runs a refactoring scan through the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> in-process, with
/// the fake providers injected into <see cref="CodingAgent.Agent.LocalConsolidationExecutor"/>, so the
/// <see cref="CodingAgent.Agent.Executors.RefactoringExecutor"/> phases, the aggregation retry and the proposal
/// validation run end to end. <see cref="RealWorkerConsolidationSmokeTests"/> proves that one proposal becomes one
/// issue; these tests cover the outcomes around it.
///
/// <para>
/// Every test scans with <see cref="RefactoringScanFocus.Structure"/>: one detector means each phase runs one agent,
/// so the order of the scripts is fixed. With <see cref="RefactoringScanFocus.All"/> four agents run in parallel and
/// share the script queue. The adversarial review stays off: with it on, one more call runs after the aggregation.
/// </para>
///
/// <para>
/// The scenario where the one selected detector fails is not covered here. Issue #4130 expects the run's error
/// to contain "All Phase 1 analysis agents failed", but the executor reports "All Phase 1 agents failed", and the
/// issue rules out adapting the assertion or the product.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerRefactoringScanTests : HeadlessE2ETestBase
{
    private const string TemplateId = "scan-template";
    private const string ScopeKey = "RefactoringDetection:" + TemplateId;

    public RealWorkerRefactoringScanTests(E2EFixture fixture) : base(fixture) { }

    // ── C1: issues ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenScanProposesTwoChanges_ShouldCreateBothIssuesWithGeneratedLabel()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        EnqueueThroughPhase2a();
        Fixture.AgentProvider.Enqueue(new RefactoringProposalsScript
        {
            Proposals =
            [
                Proposal("Split the handler", "src/Handler.cs"),
                Proposal("Rename the service", "src/Service.cs")
            ]
        });

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var created = Fixture.IssueProvider.CreatedIssues;
        created.Should().HaveCount(2);
        created.Should().ContainSingle(i => i.Title.Contains("Split the handler"));
        created.Should().ContainSingle(i => i.Title.Contains("Rename the service"));
        created.Should().OnlyContain(i => i.Labels != null && i.Labels.SequenceEqual(new[] { AgentLabels.Generated }),
            "without auto-dispatch a scan issue carries only the generated label");
        created[0].Body.Should().Contain("Split the handler description");

        AssertEachPhaseRanOnce();
        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CloneAsync));

        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == ScopeKey);
        history.RefactoringScanOutcome.Should().NotBeNull();
        history.RefactoringScanOutcome!.ProposalCount.Should().Be(2);
    }

    [Fact]
    public async Task WhenAutoDispatchIsOn_ShouldAddAgentNextToCreatedIssues()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        EnqueueThroughPhase2a();
        Fixture.AgentProvider.Enqueue(new RefactoringProposalsScript
        {
            Proposals = [Proposal("Split the handler", "src/Handler.cs")]
        });

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId, autoDispatch: true);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        Fixture.IssueProvider.CreatedIssues.Should().ContainSingle();
        Fixture.IssueProvider.CreatedIssues[0].Labels.Should().Contain(AgentLabels.Generated).And.Contain(AgentLabels.Next);
    }

    // ── C2: the aggregation retry (#3932) ─────────────────────────────────────────

    [Fact]
    public async Task WhenAggregationWritesNoFile_ShouldRetryOnceAndFileTheIssues()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        EnqueueThroughPhase2a();
        Fixture.AgentProvider
            .EnqueueCodeGen() // the aggregation exits 0 and writes nothing
            .Enqueue(new RefactoringProposalsScript
            {
                Proposals = [Proposal("Split the handler", "src/Handler.cs")]
            });

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var calls = Fixture.AgentProvider.Calls;
        calls.Should().HaveCount(5, "the aggregation runs a second time after it wrote no file");
        calls[4].Request.Prompt.Should().Be(calls[3].Request.Prompt, "the fifth call repeats the aggregation");
        Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty("every call of the scan, the retry included, is scripted");
        Fixture.IssueProvider.CreatedIssues.Should().ContainSingle();
    }

    [Fact]
    public async Task WhenAggregationWritesNoFileTwice_ShouldFailWithoutFurtherRetry()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        EnqueueThroughPhase2a();
        Fixture.AgentProvider
            .EnqueueCodeGen()
            .EnqueueCodeGen();

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        var workItem = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));

        workItem.ErrorMessage.Should().Contain("The aggregation step wrote no refactoring-proposals.json");
        Fixture.AgentProvider.Calls.Should().HaveCount(5, "the aggregation runs once more and then the run gives up");
        Fixture.IssueProvider.CreatedIssues.Should().BeEmpty();
    }

    [Fact]
    public async Task WhenAggregationWritesEmptyList_ShouldSucceedWithoutRetry()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        EnqueueThroughPhase2a();
        Fixture.AgentProvider.Enqueue(new RefactoringProposalsScript { Proposals = [] });

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        AssertEachPhaseRanOnce("an empty proposals file is a finished scan, not a broken-off call");
        Fixture.IssueProvider.CreatedIssues.Should().BeEmpty();
    }

    // ── C3: dropped proposals ─────────────────────────────────────────────────────

    [Fact]
    public async Task WhenSomeProposalsAreInvalid_ShouldDropThemAndFileTheRest()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = "7801",
            Title = "Already filed",
            Description = "",
            Labels = [],
            IsAuthorVerified = true
        });

        EnqueueThroughPhase2a();
        Fixture.AgentProvider.Enqueue(new RefactoringProposalsScript
        {
            Proposals =
            [
                Proposal("Valid change", "src/Valid.cs"),
                Proposal("No files"),
                Proposal("Already filed", "src/Dup.cs")
            ]
        });

        var agentId = $"real-worker-refactoring-{Guid.NewGuid():N}";
        var workItemId = await SeedAndClaimScanAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        Fixture.IssueProvider.CreatedIssues.Should().ContainSingle();
        Fixture.IssueProvider.CreatedIssues[0].Title.Should().Contain("Valid change");

        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == ScopeKey);
        history.RefactoringScanOutcome.Should().NotBeNull();
        var dropped = history.RefactoringScanOutcome!.DroppedProposals;
        dropped.Should().HaveCount(2);
        dropped.Should().ContainSingle(d => d.Title == "No files" && d.Reason == "no affected files");
        dropped.Should().ContainSingle(d => d.Title == "Already filed"
            && d.Reason == "title duplicates an existing issue or another proposal");
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Enqueues the three scripts that precede the aggregation: Phase 0, Phase 1 and Phase 2a. Under the Structure
    /// focus each phase runs one agent, so one script per phase is exactly what the scan consumes.
    /// </summary>
    private void EnqueueThroughPhase2a() =>
        Fixture.AgentProvider
            .EnqueueCodeGen()
            .EnqueueCodeGen()
            .EnqueueCodeGen();

    /// <summary>
    /// Asserts that Phase 0, Phase 1, Phase 2a and the aggregation each ran exactly once. The count alone is not
    /// enough: a skipped phase would hand the aggregation a <see cref="CodeGenScript"/> that writes nothing, and the
    /// executor's one retry would then consume the proposals script, still making four calls. Four distinct prompts
    /// rule that out, because a retry repeats the aggregation prompt.
    /// </summary>
    private void AssertEachPhaseRanOnce(string because = "Phase 0, Phase 1, Phase 2a and the aggregation each run once")
    {
        var prompts = Fixture.AgentProvider.Calls.Select(c => c.Request.Prompt).ToList();
        prompts.Should().HaveCount(4, because);
        prompts.Should().OnlyHaveUniqueItems("a repeated prompt means a phase was skipped and the aggregation was retried");
        Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty("every call of the scan is scripted");
    }

    private static RefactoringProposal Proposal(string title, params string[] files) => new()
    {
        Title = title,
        AffectedFiles = files,
        Description = $"{title} description",
        Rationale = "Rationale."
    };

    /// <summary>
    /// Starts the real worker for <paramref name="workItemId"/> and waits for it to finish. The hub counts every issue
    /// the scan files on the API host's badge service, which lives as long as the host and which nothing in the fixture
    /// resets. <see cref="ConsolidationPageTests"/> asserts the absolute count there, so it is reset here.
    /// </summary>
    private async Task RunWorkerAsync(string agentId, Guid workItemId)
    {
        try
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
        finally
        {
            Fixture.ApiServices.GetRequiredService<ConsolidationBadgeService>().Reset();
        }
    }

    /// <summary>
    /// Creates the refactoring-scan work item for <see cref="ScopeKey"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. The item is inserted Pending and
    /// routed first: the claim refuses an item whose payload has no pinned route, and only a Pending item can be
    /// routed. The scan runs with the Structure focus and without the adversarial review. Returns the work item GUID.
    /// </summary>
    private async Task<Guid> SeedAndClaimScanAsync(string agentId, bool autoDispatch = false, CancellationToken ct = default)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = TemplateId,
            Name = "Scan Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, ct);

        // Without the review, the queue needs no scripts for it.
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { RefactoringReviewEnabled = false }, ct);

        var workItemId = await InsertPendingWorkItemAsync(ScopeKey, agentSelector: "e2e");

        // The identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment enriches it with
        // the template's repository and issue provider configs, the pipeline configuration and the agent config.
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = ScopeKey,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-refactoring-scan-test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationTemplateId = TemplateId,
            RefactoringFocus = RefactoringScanFocus.Structure,
            AutoDispatch = autoDispatch,
            PayloadSchemaVersion = 1
        };

        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var entity = await db.WorkItems.FindAsync([workItemId], ct)
                ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");
            entity.TaskType = WorkItemTaskType.Consolidation;
            entity.IssueProviderConfigId = ConsolidationConstants.ProviderConfigId;
            entity.AgentSelector = "e2e";
            entity.ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId);
            entity.Payload = System.Text.Json.JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);
            await db.SaveChangesAsync(ct);
        }

        // Enrichment resolves the agent profile from the "e2e" selector. Seeded after the insert, which routes its
        // own profile for that selector, so the profile named here is the one the route pins.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-refactoring-scan",
            DisplayName = "Real refactoring scan profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        // Spec 056: the dispatch pins the route on the item; the claim refuses an item dispatched without one.
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, ct);
        route.Status.Should().Be(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed);

        await using (var dispatchDb = Fixture.DbContextFactory.CreateDbContext())
        {
            var dispatched = await dispatchDb.WorkItems.FindAsync([workItemId], ct)
                ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
            dispatched.Status = WorkItemStatus.Dispatched;
            dispatched.AssignedAgentId = agentId;
            dispatched.DispatchedAt = DateTimeOffset.UtcNow;
            await dispatchDb.SaveChangesAsync(ct);
        }

        // The API creates the in-memory run together with the work item (WorkItemStore.CreateAsync). A row inserted
        // straight into the database has none, and without it the hub neither keeps the scan outcome nor writes the
        // run to history. The run is added before the worker starts, so registration picks it up as it does in production.
        Fixture.RunService.AddRun(PipelineRunFactory.CreateFromWorkItem(workItemId, payload)!);

        return workItemId;
    }
}
