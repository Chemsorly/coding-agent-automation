using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The review step of an implementation run on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process
/// against the E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>. The review
/// step decides from the findings whether to start a fix agent and whether to review again. Only
/// <c>AgentPhaseExecutorCodeReviewTests</c> covered that, with a mocked provider.
///
/// <para>
/// Each test seeds one reviewer, <c>alpha</c>, and a fix prompt that carries <see cref="FixMarker"/>, so a recorded fix
/// call is told apart from a review call by its prompt. The reviewer's first call writes the findings file; every later
/// call is unscripted and returns exit 0 with no output. The findings file is deleted before each review attempt, so an
/// unscripted reviewer reports no findings.
/// </para>
///
/// <para>
/// The calls are counted by prompt, and only the review loop's own: fix prompts carry <see cref="FixMarker"/>, review
/// prompts carry the reviewer's findings path. The acceptance criteria check (on by default) runs after the reviewer in
/// every iteration, the review summary after the loop, and the cleanup, PR description and feedback calls after that.
/// None of them carries either, and the later ones can change, so the total number of calls is not asserted.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerImplementationReviewTests : HeadlessE2ETestBase
{
    /// <summary>The fix prompt the tests configure; both fix prompts start with it.</summary>
    private const string FixMarker = "E2E-REVIEW-FIX-PROMPT";

    private const string ReviewerName = "alpha";

    public RealWorkerImplementationReviewTests(E2EFixture fixture) : base(fixture) { }

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
    /// The arrange every test shares: the fix prompt and two review iterations, one reviewer, quality gates that always
    /// pass, and the scripts of the analysis, its adversarial review and code generation. The test enqueues the
    /// reviewer's findings script next, so the reviewer's first call takes it.
    /// </summary>
    private async Task ArrangeReviewRunAsync()
    {
        // A null FixPrompt means no fix agent at all, so every test has to set one.
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { CodeReview = c.CodeReview with { FixPrompt = FixMarker, MaxIterations = 2 } },
            CancellationToken.None);

        // The store has no reviewers after a reset. Empty match labels match every template.
        await Fixture.ConfigStore.SaveReviewerConfigAsync(new ReviewerConfiguration
        {
            DisplayName = "impl-reviewers",
            MatchLabels = [],
            Agents = [new ReviewAgent { Name = ReviewerName, Prompt = "review" }]
        }, CancellationToken.None);

        Fixture.QualityGateValidator.AlwaysPass();

        // Analysis, adversarial analysis review, code generation. Every later call is unscripted and returns exit 0.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();
    }

    /// <summary>Seeds an <c>agent:next</c> issue with the template its worker needs to be assigned.</summary>
    private async Task SeedIssueAsync(string issueId, string templateId)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
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
    }

    /// <summary>
    /// Claims the item for a fresh agent id and runs the real worker to completion. The worker awaits its HTTP and hub
    /// reports before it stops, so on return the completion payload is in place.
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

    /// <summary>The prompt of every agent call the worker made, in call order.</summary>
    private List<string> RecordedPrompts() =>
        Fixture.AgentProvider.Calls.Select(c => c.Request.Prompt).ToList();

    /// <summary>The fix agent's calls: both fix prompts start with the configured fix prompt.</summary>
    private int FixPromptCount() =>
        RecordedPrompts().Count(p => p.Contains(FixMarker, StringComparison.Ordinal));

    /// <summary>
    /// The reviewer's calls: the review prompt tells the reviewer to write its findings to its own file, and no other
    /// prompt names that file. The fix prompts name only the consolidated findings file.
    /// </summary>
    private int ReviewPromptCount()
    {
        var findingsPath = AgentWorkspacePaths.GetReviewFindingsFilePath(ReviewerName);
        return RecordedPrompts().Count(p =>
            p.Contains(findingsPath, StringComparison.Ordinal)
            && !p.Contains(FixMarker, StringComparison.Ordinal));
    }

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    /// <summary>Writes the reviewer's findings file, as the review prompt asks.</summary>
    private sealed class WriteFindingsScript(string findings) : AgentScript
    {
        public override void Execute(string workspacePath)
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.GetReviewFindingsFilePath(ReviewerName));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, findings);
        }
    }

    // ── Review outcomes ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The first review finds a CRITICAL. The fix agent runs once, and the review runs again: the second reviewer call is
    /// unscripted, so it finds nothing and ends the loop. The run succeeds and reports the one critical finding.
    /// </summary>
    [Fact]
    public async Task WhenReviewFindsCritical_ShouldRunFixAgentAndReviewAgain()
    {
        const string issueId = "6801";
        await ArrangeReviewRunAsync();
        Fixture.AgentProvider.Enqueue(new WriteFindingsScript("[CRITICAL] src/Foo.cs:1 — null dereference"));
        await SeedIssueAsync(issueId, "real-review-critical-template");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        FixPromptCount().Should().Be(1, "a CRITICAL finding starts one fix agent");
        ReviewPromptCount().Should().Be(2, "the CRITICAL fix is reviewed again, and the second review finds nothing");

        var payload = ReadPayload(entity.Result);
        payload.CodeReviewCriticalCount.Should().Be(1);
        payload.CodeReviewAgentsRun.Should().Contain(ReviewerName);
    }

    /// <summary>
    /// The review finds only a WARNING. The fix agent runs once and the loop ends there, although a second iteration is
    /// allowed: a warning fix is not reviewed again.
    /// </summary>
    [Fact]
    public async Task WhenReviewFindsOnlyWarning_ShouldRunFixAgentOnceAndStopReviewing()
    {
        const string issueId = "6802";
        await ArrangeReviewRunAsync();
        Fixture.AgentProvider.Enqueue(new WriteFindingsScript("[WARNING] src/Foo.cs:1 — naming"));
        await SeedIssueAsync(issueId, "real-review-warning-template");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        FixPromptCount().Should().Be(1, "a WARNING gets one fix pass");
        ReviewPromptCount().Should().Be(1, "the loop ends after the warning fix, with iterations to spare");

        var payload = ReadPayload(entity.Result);
        payload.CodeReviewWarningCount.Should().Be(1);
    }

    /// <summary>
    /// The review finds only a SUGGESTION. No fix agent runs and the loop ends after the first review. This is the
    /// behaviour of #4056: a fix round starts only for a warning or a critical.
    /// </summary>
    [Fact]
    public async Task WhenReviewFindsOnlySuggestion_ShouldNotRunFixAgent()
    {
        const string issueId = "6803";
        await ArrangeReviewRunAsync();
        Fixture.AgentProvider.Enqueue(new WriteFindingsScript("[SUGGESTION] src/Foo.cs:1 — rename this"));
        await SeedIssueAsync(issueId, "real-review-suggestion-template");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        RecordedPrompts().Should().NotContain(p => p.Contains(FixMarker, StringComparison.Ordinal),
            "a SUGGESTION starts no fix agent");
        ReviewPromptCount().Should().Be(1, "a review without a warning or a critical ends the loop");

        var payload = ReadPayload(entity.Result);
        payload.CodeReviewSuggestionCount.Should().Be(1);
    }
}
