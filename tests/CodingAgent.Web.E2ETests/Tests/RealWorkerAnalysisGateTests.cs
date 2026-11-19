using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The analysis gate on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API
/// host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// <c>ConfidenceGateTests</c> and <c>TerminalLabelOutcomeTests</c> drive these outcomes with a <c>FakeAgentClient</c>
/// and hand-written payloads, so <c>AgentPhaseExecutor</c> never runs. Here the worker runs the analysis phase, reads the
/// scripted analysis files, and reports over HTTP and the hub. Three outcomes: <c>not_ready</c>, <c>wont_do</c>, and an
/// agent that never writes <c>analysis.md</c>.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerAnalysisGateTests : HeadlessE2ETestBase
{
    public RealWorkerAnalysisGateTests(E2EFixture fixture) : base(fixture) { }

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

    /// <summary>Seeds an <c>agent:next</c> issue with the template its worker needs to be assigned.</summary>
    private async Task SeedIssueAsync(string issueId, string title, string templateId)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = title,
            Description = "## Goal\nDo something.",
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

        Fixture.QualityGateValidator.AlwaysPass();
    }

    /// <summary>
    /// Claims the item for a fresh agent id and runs the real worker to completion. The worker awaits its HTTP and hub
    /// reports before it stops, so on return the label swap and the history row are in place.
    /// </summary>
    private async Task<Guid> RunWorkerToCompletionAsync(string issueId)
    {
        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        return workItemId;
    }

    private List<(string Identifier, string Body)> CommentsOn(string issueId) =>
        Fixture.IssueProvider.PostedComments.Where(c => c.Identifier == issueId).ToList();

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    // ── not_ready ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis agent and the adversarial reviewer both say <c>not_ready</c>. The run fails at the gate with
    /// <see cref="FailureReason.GateRejected"/>, the issue ends with exactly <c>agent:needs-refinement</c>, and two
    /// comments are posted: the analysis, then the gate rejection that carries the reason.
    /// </summary>
    [Fact]
    public async Task WhenAnalysisSaysNotReady_ShouldLabelNeedsRefinementAndRecordGateRejected()
    {
        const string issueId = "6101";
        await SeedIssueAsync(issueId, "Vague issue with no acceptance criteria", "real-gate-not-ready-template");

        // First script: the analysis agent. Second script: the adversarial review agent, which writes the assessment again.
        Fixture.AgentProvider
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria")
            .EnqueueAnalysis("not_ready", "Missing acceptance criteria");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);
        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId);

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.NeedsRefinement);

        // Only the real POST /status writes FailureReason, and only for a Failed item.
        entity.FailureReason.Should().Be(FailureReason.GateRejected);
        entity.ErrorMessage.Should().Be("Analysis gate: needs refinement — Missing acceptance criteria");

        // On the Failed path the API removes the run before the hub report arrives, so the history row does not carry the
        // payload's analysis fields: those are read from the WorkItem's Result.
        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FinalLabel.Should().Be(AgentLabels.NeedsRefinement);
        payload.AnalysisRecommendation.Should().Be(AnalysisGateResult.NotReady);

        history.FinalStep.Should().Be(PipelineStep.Failed);
        history.FailureCategory.Should().Be(FailureReason.GateRejected);

        var comments = CommentsOn(issueId);
        comments.Should().HaveCount(2);
        comments.Should().ContainSingle(c => c.Body.Contains(CommentMarkers.AnalysisHeader));
        comments.Should().ContainSingle(c =>
            c.Body.Contains(CommentMarkers.GateRejection) && c.Body.Contains("Missing acceptance criteria"));

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline exits at the analysis gate");
    }

    // ── wont_do ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis agent and the adversarial reviewer both say <c>wont_do</c>. The run ends Completed, so the WorkItem
    /// is Succeeded, the issue ends with exactly <c>agent:wont-do</c>, and no pull request is created.
    ///
    /// <para>
    /// <c>FailureReason</c> is not asserted: <c>ApplyStatusMutation</c> writes it only for a Failed item, so a won't-do
    /// WorkItem has none.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenAnalysisSaysWontDo_ShouldLabelWontDoAndSucceedWithoutPullRequest()
    {
        const string issueId = "6102";
        await SeedIssueAsync(issueId, "Add something that already exists", "real-gate-wont-do-template");

        Fixture.AgentProvider
            .EnqueueAnalysis("wont_do", "Already implemented")
            .EnqueueAnalysis("wont_do", "Already implemented");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        // The Succeeded history row is written by the hub report, ahead of its label swap; the worker has already
        // returned, so both are done.
        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);
        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed);

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.WontDo);

        history.AnalysisRecommendation.Should().Be(AnalysisGateResult.WontDo);

        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Completed);
        payload.FinalLabel.Should().Be(AgentLabels.WontDo);

        var comments = CommentsOn(issueId);
        comments.Should().HaveCount(2);
        comments.Should().ContainSingle(c => c.Body.Contains(CommentMarkers.AnalysisHeader));
        comments.Should().ContainSingle(c => c.Body.Contains(CommentMarkers.GateWontDo));

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a won't-do run ends at the analysis gate");
    }

    // ── analysis never written ──────────────────────────────────────────────────────

    /// <summary>
    /// The agent exits 0 and writes nothing. The pipeline retries the analysis until the attempts are used up
    /// (<c>MaxAnalysisRetries</c> defaults to 2, so 3 attempts) and ends with <c>agent:needs-refinement</c>.
    ///
    /// <para>
    /// <c>FailureReason</c> is not asserted: this path sets no failure category, so the persisted value is the resolver's
    /// default, not something the analysis gate decides.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenAnalysisFileIsNeverWritten_ShouldRetryThenLabelNeedsRefinement()
    {
        const string issueId = "6103";
        await SeedIssueAsync(issueId, "The agent writes no analysis", "real-gate-no-analysis-template");

        // No scripts: an unscripted call returns exit 0 and writes nothing.
        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.NeedsRefinement);

        entity.ErrorMessage.Should().Be("Analysis failed after 3 attempt(s): analysis.md not found after agent execution");

        // One call per attempt; no review follows an analysis that was never written.
        Fixture.AgentProvider.UnscriptedCalls.Should().HaveCount(3);

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline exits when the analysis cannot be read");
    }
}
