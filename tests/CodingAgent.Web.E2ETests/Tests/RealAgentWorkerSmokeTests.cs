using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke tests that run the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> in-process
/// against the E2E API host.
///
/// <para>
/// Unlike all other E2E tests, where the agent side is a <see cref="FakeAgentClient"/> posting
/// canned payloads, these tests use the real worker: real SignalR connection, real MessagePack
/// protocol, real pipeline execution, real hub method calls — with only the I/O boundaries
/// substituted (scripted agent provider + in-memory repository + quality gate validator fake).
/// </para>
///
/// <para>
/// This is the guard against wire-contract drift. The
/// <c>RequestGetIssue</c>/<c>RequestListComments</c>/<c>RequestUpdateComment</c> MessagePack
/// binding failure broke decomposition from 2026-07-23 to 2026-09-23 while all other tests
/// stayed green. Reverting <see cref="AgentHubMessagePack.SerializerOptions"/>
/// (specifically removing <c>IssueIdentifierFormatter</c>) would make Scenario B fail immediately.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealAgentWorkerSmokeTests : HeadlessE2ETestBase
{
    public RealAgentWorkerSmokeTests(E2EFixture fixture) : base(fixture) { }

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

        // Set AssignedAgentId and a minimal-but-valid payload so the API's GetAssignment endpoint
        // can deserialize it (JobDistributionRequest has required properties that fail with "{}").
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.Status = WorkItemStatus.Dispatched;
        entity.AssignedAgentId = agentId;
        entity.DispatchedAt = DateTimeOffset.UtcNow;
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
                ?? new IssueDetail { Identifier = issueId, Title = "", Description = "", Labels = [] },
            PayloadSchemaVersion = 1
        };

        // Enrichment resolves the agent profile from the "e2e" selector.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-agent",
            DisplayName = "Real agent smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
            Enabled = true
        });
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(
            minimalPayload, CodingAgent.Pipeline.PipelineJsonOptions.Default);

        await db.SaveChangesAsync(ct);

        return workItemId;
    }

    // ── Scenario A: implementation happy path ────────────────────────────────────────

    /// <summary>
    /// A real agent worker fetches an <c>agent:next</c> issue, runs the full analysis →
    /// code-generation → quality-gate pipeline using scripted outputs, and completes the
    /// work item successfully.
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item>The WorkItem ends <c>Succeeded</c>.</item>
    ///   <item>A PR was created on <see cref="InMemoryRepositoryProvider"/>.</item>
    ///   <item>The issue ends with exactly <c>agent:done</c>.</item>
    ///   <item>The analysis comment was posted through the hub.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ScenarioA_ImplementationHappyPath_WorkItemSucceeds()
    {
        const string issueId = "5001";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Seed the issue with agent:next label
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] All methods validate inputs",
            Labels = ["agent:next"]
        });

        // Template + profile so dispatch resolves correctly
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-agent-template",
            Name = "Real Agent Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-agent-profile",
            DisplayName = "Real Agent Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
            Enabled = true
        });

        // Queue scripted outputs for the pipeline phases:
        // Phase 1 analysis: writes analysis.md + analysis-assessment.json (recommendation=ready)
        // Phase 1 review: adversarial review agent (also analysis script, just writes files)
        // Phase 2 code gen: implementation agent
        // Phase 2 PR desc: PR description agent (final step after QG pass)
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()  // analysis agent
            .EnqueueReadyAnalysis()  // adversarial review agent
            .EnqueueCodeGen()        // code generation agent
            .EnqueueCodeGen();       // PR description agent

        // Quality gates always pass
        Fixture.QualityGateValidator.AlwaysPass();

        // ── Create + claim work item with our agent ID ─────────────────────────────

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        // ── Start real agent worker ────────────────────────────────────────────────

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        // ── Wait for completion ────────────────────────────────────────────────────

        await harness.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(20));

        var finalItem = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded,
            timeout: TimeSpan.FromSeconds(15));
        finalItem.CompletedAt.Should().NotBeNull();
        finalItem.ErrorMessage.Should().BeNull();
        // Only the real POST /status writes Result; a direct DB write never did.
        finalItem.Result.Should().NotBeNullOrEmpty();
        var payload = System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(finalItem.Result!, PipelineJsonOptions.Default);
        payload!.FinalStep.Should().Be(PipelineStep.Completed);

        // PR was created on InMemoryRepositoryProvider
        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline must create a PR after successful quality gates");
        Fixture.RepositoryProvider.MethodCalls.Count(m => m == nameof(IRepositoryProvider.CreatePullRequestAsync)).Should().Be(1);

        // Issue label ends with agent:done
        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issue.Labels.Should().Contain(AgentLabels.Done);
        issue.Labels.Should().NotContain(AgentLabels.Next);
        issue.Labels.Should().NotContain(AgentLabels.InProgress);
        issue.Labels.Should().NotContain(AgentLabels.Error);

        // Analysis comment was posted through the hub
        Fixture.IssueProvider.PostedComments.Should().Contain(c =>
            c.Identifier == issueId && c.Body.Contains(CommentMarkers.AnalysisHeader),
            "the analysis step must post a comment with the analysis header");
    }

    // ── Scenario B: decomposition Phase 1 ─────────────────────────────────────────

    /// <summary>
    /// A real agent worker runs the decomposition Phase 1 analysis for an <c>agent:epic</c>
    /// issue. The plan comment is posted through the hub, and the label transitions to
    /// <c>agent:epic-review</c>.
    ///
    /// A second Phase 1 run updates the same comment (uses <see cref="InMemoryIssueProvider.UpdatedComments"/>)
    /// instead of posting a second one. This exercises the <see cref="AgentHubMessagePack"/>
    /// wire contract for the <c>RequestUpdateComment</c> hub method — the binding failure that
    /// was invisible to unit tests.
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item>The plan comment carrying <see cref="CommentMarkers.DecompositionPlan"/> is posted.</item>
    ///   <item>The label ends at <c>agent:epic-review</c>.</item>
    ///   <item>A second Phase 1 run updates the same comment instead of posting a second one.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ScenarioB_DecompositionPhase1_PostsPlanCommentAndSetsEpicReviewLabel()
    {
        const string issueId = "5002";

        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Epic: Implement feature X",
            Description = "## Goal\nBuild feature X end-to-end",
            Labels = ["agent:epic"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "decomp-agent-template",
            Name = "Decomp Agent Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            DecompositionEnabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "decomp-agent-profile",
            DisplayName = "Decomp Agent Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
            Enabled = true
        });

        // ── First Phase 1 run ───────────────────────────────────────────────────────

        // Phase 1 scripts: decomposition plan agent + adversarial review agent
        Fixture.AgentProvider
            .EnqueueDecompositionPlan()  // primary decomposition analysis agent
            .EnqueueDecompositionPlan(); // adversarial review agent

        var agentId1 = $"decomp-worker1-{Guid.NewGuid():N}";
        var workItemId1 = await CreateAndClaimWorkItemAsync(issueId, agentId1,
            taskType: WorkItemTaskType.Decomposition);

        await using var harness1 = new RealAgentWorkerHarness();
        await harness1.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId1,
            workItemId: workItemId1.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness1.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(20));

        var firstItem = await WaitForWorkItemStatusAsync(workItemId1, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        firstItem.Result.Should().NotBeNullOrEmpty();

        // Assert: plan comment was posted
        Fixture.IssueProvider.PostedComments.Should().Contain(c =>
            c.Identifier == issueId && c.Body.Contains(CommentMarkers.DecompositionPlan),
            "the decomposition analysis step must post a plan comment with the marker");

        // Assert: label transitioned to agent:epic-review
        var issueAfterPhase1 = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issueAfterPhase1.Labels.Should().Contain(AgentLabels.EpicReview,
            "the pipeline must set agent:epic-review after a successful Phase 1 run");

        // ── Second Phase 1 run (should UPDATE, not create new comment) ─────────────

        // Reset label to agent:epic so a second dispatch is valid
        await Fixture.IssueProvider.RemoveLabelAsync(issueId, AgentLabels.EpicReview, CancellationToken.None);
        await Fixture.IssueProvider.AddLabelsAsync(issueId, ["agent:epic"], CancellationToken.None);

        // Enqueue scripts for second run
        Fixture.AgentProvider
            .EnqueueDecompositionPlan()
            .EnqueueDecompositionPlan();

        var agentId2 = $"decomp-worker2-{Guid.NewGuid():N}";
        var workItemId2 = await CreateAndClaimWorkItemAsync(issueId, agentId2,
            taskType: WorkItemTaskType.Decomposition);

        await using var harness2 = new RealAgentWorkerHarness();
        await harness2.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId2,
            workItemId: workItemId2.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness2.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(20));

        var secondItem = await WaitForWorkItemStatusAsync(workItemId2, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        secondItem.Result.Should().NotBeNullOrEmpty();

        // Assert: second run updates the existing comment, not posts a second one.
        // UpdatedComments is populated when UpdateCommentAsync is called, which the pipeline uses
        // when it detects an existing plan comment. This proves the RequestUpdateComment hub
        // method's IssueIdentifierFormatter wire contract is exercised end-to-end.
        Fixture.IssueProvider.UpdatedComments.Should().Contain(
            c => c.Identifier == issueId && c.NewBody.Contains(CommentMarkers.DecompositionPlan),
            "the second Phase 1 run must update the existing plan comment via RequestUpdateComment");

        // Only one plan comment should exist (same comment updated, not duplicated)
        var planCommentCount = Fixture.IssueProvider.PostedComments.Count(c =>
            c.Identifier == issueId && c.Body.Contains(CommentMarkers.DecompositionPlan));
        planCommentCount.Should().Be(1,
            "the second run must reuse the existing plan comment (update), not post a second one");

        // Label ends at agent:epic-review again after second run
        var issueAfterPhase1Run2 = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issueAfterPhase1Run2.Labels.Should().Contain(AgentLabels.EpicReview,
            "the second Phase 1 run must also set agent:epic-review");
    }

    // ── Scenario C: analysis not-ready gate rejection ────────────────────────────────

    /// <summary>
    /// A real agent worker runs an analysis that returns not_ready on both the primary and
    /// adversarial review passes. The pipeline records <c>GateRejected</c> through the real
    /// HTTP status channel, proving that <c>FailureReason</c> is written by
    /// <see cref="CodingAgent.Api.WorkItemStatusTransitionService"/> (the old direct DB client
    /// never set this field).
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item><c>FailureReason</c> is <c>GateRejected</c> — only the real HTTP path writes it.</item>
    ///   <item><c>Result</c> is non-null — serialized by <see cref="CodingAgent.Agent.HttpPrimaryCompletionReporter"/>.</item>
    ///   <item><c>ErrorMessage</c> contains "Analysis gate: needs refinement".</item>
    ///   <item>The issue label ends at <c>agent:needs-refinement</c>, not <c>agent:error</c>.</item>
    ///   <item>No PR was created.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task ScenarioC_AnalysisNotReady_RecordsGateRejectedThroughHttpStatus()
    {
        const string issueId = "5003";

        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier = issueId,
            Title = "Vague issue with no acceptance criteria",
            Description = "## Goal\nDo something.",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-agent-template-c",
            Name = "Real Agent Template C",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-agent-profile-c",
            DisplayName = "Real Agent Profile C",
            JobTemplate = E2ERouting.Key(["e2e"]),
            Enabled = true
        });

        // First script: primary analysis agent returns not_ready.
        // Second script: adversarial review agent also returns not_ready.
        // The pipeline exits GateRejected before code generation, so the empty queue is never reached.
        Fixture.AgentProvider
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria")
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria");

        Fixture.QualityGateValidator.AlwaysPass();

        // ── Create + claim work item ───────────────────────────────────────────────

        var agentId = $"real-worker-c-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        // ── Start real agent worker ────────────────────────────────────────────────

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        // ── Wait for completion ────────────────────────────────────────────────────

        await harness.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(20));

        // ── Assert ─────────────────────────────────────────────────────────────────

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(15));

        // Only the real POST /status path writes FailureReason via ApplyStatusMutation.
        // A mock that returns true without calling HTTP leaves this field null.
        item.FailureReason.Should().Be(FailureReason.GateRejected);

        // Result is serialized by HttpPrimaryCompletionReporter only on the real HTTP path.
        item.Result.Should().NotBeNullOrEmpty();

        // HandleNotReadyGateAsync sets: $"Analysis gate: needs refinement — {assessment.Reason}"
        item.ErrorMessage.Should().Contain("Analysis gate: needs refinement");

        // FailRunWithLabelAsync sets agent:needs-refinement (not agent:error).
        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issue.Labels.Should().Contain(AgentLabels.NeedsRefinement);
        issue.Labels.Should().NotContain(AgentLabels.Error);

        // No PR was created — the pipeline exited at the analysis gate.
        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync));
    }
}
