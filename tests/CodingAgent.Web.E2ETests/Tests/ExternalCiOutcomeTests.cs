using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The outcomes of external CI on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the
/// E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/> and the scripted CI of
/// <see cref="InMemoryPipelineProvider"/> as in <see cref="ScriptedCiSmokeTests"/>.
///
/// <para>
/// The rule every test checks: only a real code failure uses the agent's retry budget. An error reading the CI status,
/// CI that never starts and an exhausted status read end without the fix agent, so <c>RetryCount</c> stays 0. A code
/// failure and a CI timeout are plain gate failures, so the fix agent runs once and <c>RetryCount</c> is 1.
/// <c>RetryCount</c> rises only when the fix agent actually runs, which makes it the proof that no fix agent ran.
/// </para>
///
/// <para>
/// A round of the CI queue is consumed per <c>WaitForCompletionAsync</c>, and a run waits for CI more than once: for the
/// first gate, after each fix, after the cleanup, and after the pull request. An empty queue means pass, so each test
/// queues only the rounds that matter and none of them asserts an exact number of waits.
/// </para>
///
/// <para>
/// The harness runs every worker under one workspace root, so every test uses its own issue number.
/// <c>FailureReason</c> and <c>FailureCategory</c> are not asserted for the runs that end in a draft pull request: the
/// message, the retry count and the label are the stable signals. Terminal states during a poll, refused pushes and
/// infrastructure logs are covered elsewhere.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ExternalCiOutcomeTests : HeadlessE2ETestBase
{
    public ExternalCiOutcomeTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an Implementation work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. A copy of the helper in
    /// <see cref="ScriptedCiSmokeTests"/>: the payload names the pipeline provider, which
    /// <c>HeadlessE2ETestBase.DispatchIssueAsync</c> cannot do. Returns the claimed work item GUID.
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
        // enriches it with configs. PipelineProviderConfigId is one half of the pipeline provider wiring: the
        // enrichment copies it to the assignment, and adds the matching ProviderConfig from the store.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            PipelineProviderConfigId = "pipeline-e2e",
            InitiatedBy = "e2e-external-ci-test",
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
            Id = "profile-e2e-external-ci-outcome",
            DisplayName = "External CI outcome profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });
        entity.Payload = System.Text.Json.JsonSerializer.Serialize(
            minimalPayload, PipelineJsonOptions.Default);

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
    /// The arrangement every test shares: the pipeline provider config the worker needs to get the fake CI, an
    /// <c>agent:next</c> issue with the template that names the provider, passing local quality gates, and the scripts
    /// of the first three agent calls (analysis, analysis review, code generation). Later agent calls are unscripted
    /// and return exit 0.
    /// </summary>
    private async Task ArrangeAsync(string issueId)
    {
        // The config half of the wiring: the enrichment adds this to the assignment's ProviderConfigs only when
        // the store has it. The store's Reset() removes it again before the next test.
        await Fixture.ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "pipeline-e2e", Kind = ProviderKind.Pipeline, ProviderType = "GitHub", DisplayName = "E2E CI" },
            CancellationToken.None);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] All methods validate inputs",
            Labels = [AgentLabels.Next]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "external-ci-outcome-template",
            Name = "External CI Outcome Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            PipelineProviderId = "pipeline-e2e",
            Enabled = true
        }, CancellationToken.None);

        Fixture.QualityGateValidator.AlwaysPass();

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()  // analysis agent
            .EnqueueReadyAnalysis()  // adversarial review agent
            .EnqueueCodeGen();       // code generation agent
    }

    /// <summary>
    /// Claims a work item for <paramref name="issueId"/>, runs the real worker on it to completion, and returns the
    /// terminal row with the payload the worker reported. <paramref name="config"/> changes the pipeline configuration
    /// before the run; the poll interval is always short, or the post-PR wait would sleep for 30 s.
    /// </summary>
    private async Task<(WorkItemEntity Item, JobCompletionPayload Payload)> RunWorkerAsync(
        string issueId,
        WorkItemStatus expected,
        Func<PipelineConfiguration, PipelineConfiguration>? config = null)
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => (config?.Invoke(c) ?? c) with { ExternalCiPollInterval = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);

        var agentId = $"external-ci-worker-{Guid.NewGuid():N}";
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

        var item = await WaitForWorkItemStatusAsync(workItemId, expected, timeout: TimeSpan.FromSeconds(10));

        item.Result.Should().NotBeNullOrEmpty("only the real HTTP terminal report writes Result");
        var payload = System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(item.Result!, PipelineJsonOptions.Default)!;
        return (item, payload);
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
    /// The scripted fix agent call: records what the run left in the quality gates output directory, which is where the
    /// failed CI job logs are written for the agent. The run's workspace is deleted when it ends, so the files are read
    /// during the call. It must not throw: the pipeline would read that as a transient error and wait.
    /// </summary>
    private sealed class CapturingFixScript : AgentScript
    {
        /// <summary>The content of every file under the quality gates output directory, one after the other.</summary>
        public string Captured { get; private set; } = "";

        public override void Execute(string workspacePath)
        {
            var directory = Path.Combine(workspacePath, AgentWorkspacePaths.QualityGatesOutputDirectory);
            if (!Directory.Exists(directory))
                return;

            Captured = string.Join(
                "\n",
                Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Select(File.ReadAllText));
        }
    }

    // ── CI passes ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// CI passes on the first round. No fix agent runs, so <c>RetryCount</c> is 0, and the pull request ends ready for
    /// review with <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenCiPasses_ShouldProceedWithoutRetry()
    {
        const string issueId = "7401";
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeAsync(issueId);
        ci.EnqueuePass();

        var (item, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        item.Status.Should().Be(WorkItemStatus.Succeeded);
        payload.RetryCount.Should().Be(0);
        payload.IsDraftPr.Should().BeFalse();
        ci.WaitCalls.Should().NotBeEmpty("the pipeline must wait for external CI when a pipeline provider is wired");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// The first CI round fails on a compile error. That is a code failure, so the fix agent runs once, with the CI job
    /// log written to the quality gates output directory, and the next round passes.
    ///
    /// <para>
    /// The fix call is the fourth agent call (after analysis, analysis review and code generation), so the capturing
    /// script is queued fourth. A fix call that did not find the log would leave <c>Captured</c> empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCiFailsOnCode_ShouldRunFixRoundWithLogsThenPass()
    {
        const string issueId = "7402";
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeAsync(issueId);
        ci.EnqueueFailure("unit", 42, "error CS1002: ; expected\nBuild FAILED.").EnqueuePass();
        var fixAgent = new CapturingFixScript();
        Fixture.AgentProvider.Enqueue(fixAgent);

        var (item, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        item.Status.Should().Be(WorkItemStatus.Succeeded);
        payload.RetryCount.Should().Be(1);
        payload.IsDraftPr.Should().BeFalse();
        ci.WaitCalls.Count.Should().BeGreaterThanOrEqualTo(2, "the failed round and the round after the fix");
        fixAgent.Captured.Should().Contain("error CS1002",
            "the fix agent reads the CI job log from the quality gates output directory");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    // ── Errors reading the CI status (#3909) ────────────────────────────────────────

    /// <summary>
    /// Reading the CI status fails once. The pipeline polls again without charging the agent: the failure is the
    /// provider's, not the code's, so no fix agent runs and <c>RetryCount</c> stays 0.
    /// </summary>
    [Fact]
    public async Task WhenReadingCiStatusFailsOnce_ShouldPollAgainWithoutChargingTheAgent()
    {
        const string issueId = "7403";
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeAsync(issueId);
        ci.EnqueueError(new HttpRequestException("503 from CI provider")).EnqueuePass();

        var (item, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        item.Status.Should().Be(WorkItemStatus.Succeeded);
        payload.RetryCount.Should().Be(0);
        ci.WaitCalls.Count.Should().BeGreaterThanOrEqualTo(2, "the failed read and the poll after it");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// Reading the CI status fails on every attempt. With one infrastructure retry that is two failed reads. The run
    /// ends Failed with a draft pull request and <c>agent:error</c>, and no fix agent runs, so <c>RetryCount</c> is 0.
    /// </summary>
    [Fact]
    public async Task WhenReadingCiStatusAlwaysFails_ShouldEndWithDraftPrWithoutFixAgent()
    {
        const string issueId = "7404";
        await ArrangeAsync(issueId);
        Fixture.FakeProviders.PipelineProvider
            .EnqueueError(new HttpRequestException("503 from CI provider"))
            .EnqueueError(new HttpRequestException("503 from CI provider"));

        var (item, payload) = await RunWorkerAsync(
            issueId, WorkItemStatus.Failed, c => c with { MaxInfrastructureRetries = 1 });

        item.Status.Should().Be(WorkItemStatus.Failed);
        payload.RetryCount.Should().Be(0);
        payload.IsDraftPr.Should().BeTrue();
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        item.ErrorMessage.Should().Be("Quality gates failed after max retries; draft PR created.");
        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync));
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }

    // ── CI that never starts, and CI that times out ─────────────────────────────────

    /// <summary>
    /// CI never starts for the pushed commit. The pipeline re-pushes once, then gives up with an infrastructure failure
    /// that names the retries. The run ends Failed with a draft pull request and <c>agent:error</c>, and no fix agent
    /// runs: the code is not what is wrong, so <c>RetryCount</c> stays 0.
    ///
    /// <para>
    /// <c>FailureReason</c> and <c>FailureCategory</c> are not asserted: the code sets the category on the not-started
    /// path and again when it finalizes the draft, and a test must not pin either.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCiNeverStarts_ShouldRepushThenEndWithDraftPrWithoutFixAgent()
    {
        const string issueId = "7405";
        await ArrangeAsync(issueId);
        Fixture.FakeProviders.PipelineProvider.EnqueueNotStarted();

        var (item, payload) = await RunWorkerAsync(
            issueId,
            WorkItemStatus.Failed,
            c => c with { CiNotStartedTimeout = TimeSpan.FromMilliseconds(200), CiNotStartedMaxRetries = 1 });

        item.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Be("CI never started after 1 retries");
        payload.RetryCount.Should().Be(0);
        payload.IsDraftPr.Should().BeTrue();
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }

    /// <summary>
    /// The first CI wait never returns and is cancelled when <c>ExternalCiTimeout</c> runs out. A timeout is a plain gate
    /// failure, not an infrastructure failure, so the fix agent runs once and the next round passes.
    /// </summary>
    [Fact]
    public async Task WhenCiTimesOut_ShouldCountAsGateFailureAndRunFixRound()
    {
        const string issueId = "7406";
        await ArrangeAsync(issueId);
        Fixture.FakeProviders.PipelineProvider.EnqueueHang().EnqueuePass();

        var (item, payload) = await RunWorkerAsync(
            issueId, WorkItemStatus.Succeeded, c => c with { ExternalCiTimeout = TimeSpan.FromMilliseconds(500) });

        item.Status.Should().Be(WorkItemStatus.Succeeded);
        payload.RetryCount.Should().Be(1);
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }
}
