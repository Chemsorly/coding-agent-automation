using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The feedback the agent gives after a run, on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process
/// against the E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// <c>FeedbackFlowTests</c> hands <c>RunFeedback</c> to the hub in a canned payload, so nothing there tests that the worker
/// parses the agent's output. Here the feedback call is a scripted agent output line. The worker parses it, reports it over
/// the hub, and the API's fast path posts the comment, completes the outbox row and links the comment from the PR body.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root. The Scheduler's
/// relay is not started: the fast path in the API is the only thing that posts here.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerFeedbackTests : HeadlessE2ETestBase
{
    private const string IssueFeedbackDescription = "The issue misses a test plan.";

    private const string FeedbackWithIssueLine =
        "{\"harness\":{\"category\":\"e2e-harness\"},\"issue\":{\"category\":\"e2e-issue\",\"description\":\"" + IssueFeedbackDescription + "\"}}";

    private const string FeedbackWithoutIssueLine = "{\"harness\":{\"category\":\"e2e-harness\"}}";

    public RealWorkerFeedbackTests(E2EFixture fixture) : base(fixture) { }

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

    /// <summary>
    /// Seeds an <c>agent:next</c> issue as in Scenario A of <see cref="RealAgentWorkerSmokeTests"/>, scripts a run whose
    /// feedback call outputs <paramref name="feedbackLine"/>, and runs the real worker to completion.
    ///
    /// <para>
    /// After the code generation script come four copies of the one-line script. They cover the cleanup, PR description
    /// and feedback calls, whichever order those come in, so no test counts calls to find the feedback one. No
    /// <c>pr-description.md</c> is written: that step only logs a warning. Unused scripts are cleared by <c>Reset</c>.
    /// </para>
    ///
    /// <para>
    /// The terminal HTTP status and the history row are written before the hub bookkeeping (comment, outbox row, PR body),
    /// so only the worker's completion is a barrier for what the caller reads next.
    /// </para>
    /// </summary>
    private async Task<Guid> RunWorkerWithFeedbackAsync(string issueId, string templateId, string feedbackLine)
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

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()  // analysis agent
            .EnqueueReadyAnalysis()  // adversarial review agent
            .EnqueueCodeGen();       // code generation agent
        for (var i = 0; i < 4; i++)
            Fixture.AgentProvider.Enqueue(new CodeGenScript { OutputLines = [feedbackLine] });

        Fixture.QualityGateValidator.AlwaysPass();

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

    private List<(string Identifier, string Body)> FeedbackCommentsOn(string issueId) =>
        Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId && c.Body.Contains(CommentMarkers.IssueFeedback))
            .ToList();

    // ── feedback about the issue ────────────────────────────────────────────────────

    /// <summary>
    /// The agent's feedback names a problem with the issue. The worker parses it from the agent's output line, and the
    /// run's history row carries it. The API's fast path then posts exactly one feedback comment on the issue, completes
    /// the outbox row, and links that comment from the pull request body.
    /// </summary>
    [Fact]
    public async Task WhenAgentGivesIssueFeedback_ShouldPostOnceCompleteOutboxAndLinkFromPullRequest()
    {
        const string issueId = "6601";

        var workItemId = await RunWorkerWithFeedbackAsync(issueId, "real-feedback-issue-template", FeedbackWithIssueLine);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);
        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId);

        // The worker parsed the feedback from the agent's output line.
        history.Feedback.Should().NotBeNull();
        history.Feedback!.Issue.Should().NotBeNull();
        history.Feedback.Issue!.Description.Should().Be(IssueFeedbackDescription);
        history.Feedback.Harness.Category.Should().Be("e2e-harness");

        // One feedback comment, posted by the API's fast path.
        var feedbackComments = FeedbackCommentsOn(issueId);
        feedbackComments.Should().ContainSingle();
        feedbackComments.Single().Body.Should().Contain(IssueFeedbackDescription);

        // The delivered comment completes its outbox row.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.FeedbackCommentOutbox.Single(f => f.RunId == workItemId.ToString()).Status.Should().Be("Completed");

        // The pull request body links the comment. The fake's PR URL ends in /pull/1. The comment id is not asserted:
        // the analysis comment is posted first.
        Fixture.RepositoryProvider.PullRequestBodies.Should().ContainKey(1,
            "the hub appends the feedback link to the body of the PR the run created");
        var pullRequestBody = Fixture.RepositoryProvider.PullRequestBodies[1];
        pullRequestBody.Should().Contain("## Agent Feedback");
        pullRequestBody.Should().Contain($"https://github.com/test/repo/issues/{issueId}#issuecomment-");
    }

    // ── no feedback about the issue ─────────────────────────────────────────────────

    /// <summary>
    /// The agent's feedback has only the harness part. The run succeeds, no feedback comment is posted, and no outbox row
    /// is created. The history row proves the feedback call ran and was parsed: without that, the negative assertions
    /// would also hold if the call never happened.
    /// </summary>
    [Fact]
    public async Task WhenAgentGivesNoIssueFeedback_ShouldNotCreateOutboxRow()
    {
        const string issueId = "6602";

        var workItemId = await RunWorkerWithFeedbackAsync(issueId, "real-feedback-none-template", FeedbackWithoutIssueLine);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);
        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId);

        // Positive control: the feedback was parsed, and it has no issue part.
        history.Feedback.Should().NotBeNull();
        history.Feedback!.Harness.Category.Should().Be("e2e-harness");
        history.Feedback.Issue.Should().BeNull();

        FeedbackCommentsOn(issueId).Should().BeEmpty();

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.FeedbackCommentOutbox.Any(f => f.RunId == workItemId.ToString()).Should().BeFalse();
    }
}
