using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Tests of the scripted CI rounds in <see cref="InMemoryPipelineProvider"/>, plus one run of the
/// real <see cref="CodingAgent.Agent.WorkItemAgentService"/> that polls the fake provider.
///
/// <para>
/// The worker only gets a pipeline provider when two things are in place: the work item payload
/// names it (<c>JobDistributionRequest.PipelineProviderConfigId</c>), and the config store holds a
/// <see cref="ProviderKind.Pipeline"/> config with that id. Missing either one is silent: the run
/// skips external CI. The real-worker test therefore asserts on the calls the fake recorded, not
/// only on the final label.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ScriptedCiSmokeTests : HeadlessE2ETestBase
{
    public ScriptedCiSmokeTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an Implementation work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. Same as the helper in
    /// <c>RealAgentWorkerSmokeTests</c>, except that the payload names the pipeline provider.
    /// Returns the claimed work item GUID.
    ///
    /// <para>
    /// <c>HeadlessE2ETestBase.DispatchIssueAsync</c> cannot be used for CI runs: it passes
    /// <c>PipelineProviderId = null</c>.
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
        // enriches it with configs. PipelineProviderConfigId is one half of the pipeline provider wiring: the
        // enrichment copies it to the assignment, and adds the matching ProviderConfig from the store.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            PipelineProviderConfigId = "pipeline-e2e",
            InitiatedBy = "e2e-smoke-test",
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
            Id = "profile-e2e-scripted-ci",
            DisplayName = "Scripted CI smoke profile",
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

    // ── The fake, without a worker ──────────────────────────────────────────────────

    // TODO [WARNING] (TestQualityReviewer): Reset() is untested, although the fixture relies on it to isolate tests
    // (FakeProviderFactory.Reset() calls it between tests). A test that enqueues rounds and fills JobLogs, calls
    // Reset(), then asserts the default pass and empty StatusCalls/WaitCalls/JobLogs and ValidateCalls == 0 would pin
    // it; a dropped _rounds.Clear() would otherwise leak queued failures or a NotStarted round into every later E2E
    // test. Not added here: a sixth test conflicts with the issue's acceptance criterion of exactly five tests.
    // TODO [WARNING] (TestQualityReviewer): The refactored empty-queue path is covered for ShouldPass = true and
    // false, but SimulatedDelay is not: the old WaitForCompletionAsync delayed twice (its own delay plus the one in
    // GetRunStatusAsync), the new one delays once, and no test asserts either. Same reason as above.
    [Fact]
    public async Task WhenFailureThenPassEnqueued_ShouldServeRoundsInOrderThenFallBackToDefault()
    {
        var ci = Fixture.FakeProviders.PipelineProvider;
        ci.EnqueueFailure("unit", 42, "boom").EnqueuePass();

        // A status read shows the head round without consuming it.
        var peek = await ci.GetRunStatusAsync("feature/auto-1-x", "sha-1", CancellationToken.None);

        var first = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        var second = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-2", TimeSpan.FromMinutes(1), CancellationToken.None);
        var third = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-3", TimeSpan.FromMinutes(1), CancellationToken.None);

        // The queue has drained: the default answers from ShouldPass again.
        ci.ShouldPass = false;
        var fourth = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-4", TimeSpan.FromMinutes(1), CancellationToken.None);

        peek.State.Should().Be(PipelineRunState.Failed);
        first.State.Should().Be(PipelineRunState.Failed);
        first.Jobs[0].JobId.Should().Be(42);
        first.Jobs[0].LogContent.Should().Be("boom");
        second.State.Should().Be(PipelineRunState.Passed);
        third.State.Should().Be(PipelineRunState.Passed);
        third.Jobs[0].Name.Should().Be("build-and-test");
        fourth.State.Should().Be(PipelineRunState.Failed);
        fourth.Jobs[0].Name.Should().Be("build-and-test");
        fourth.Jobs[0].FailureReason.Should().Be("Tests failed");
        (await ci.GetJobLogsAsync(42, CancellationToken.None)).Should().Be("boom");

        ci.WaitCalls.Select(c => c.CommitSha).Should().Equal("sha-1", "sha-2", "sha-3", "sha-4");
        ci.WaitCalls.Should().OnlyContain(c => c.Branch == "feature/auto-1-x");
        ci.StatusCalls.Should().HaveCount(1, "only the explicit status read counts; a wait must not count as a status read");
    }

    [Fact]
    public async Task WhenNotStartedEnqueued_ShouldStayPendingWithoutJobs()
    {
        var ci = Fixture.FakeProviders.PipelineProvider;
        ci.EnqueueNotStarted();

        var first = await ci.GetRunStatusAsync("feature/auto-1-x", "sha-1", CancellationToken.None);

        // A wait must report Pending too and must not consume the round: the status read after it still sees it.
        var waited = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        var second = await ci.GetRunStatusAsync("feature/auto-1-x", "sha-1", CancellationToken.None);

        first.State.Should().Be(PipelineRunState.Pending);
        first.Jobs.Should().BeEmpty();
        waited.State.Should().Be(PipelineRunState.Pending);
        waited.Jobs.Should().BeEmpty();
        second.State.Should().Be(PipelineRunState.Pending);
        second.Jobs.Should().BeEmpty();
        ci.StatusCalls.Should().HaveCount(2);
        ci.WaitCalls.Should().HaveCount(1);
    }

    [Fact]
    public async Task WhenErrorEnqueued_ShouldThrowFromWaitOnly()
    {
        var ci = Fixture.FakeProviders.PipelineProvider;
        ci.EnqueueError(new HttpRequestException("503"));

        var status = await ci.GetRunStatusAsync("feature/auto-1-x", "sha-1", CancellationToken.None);
        status.State.Should().Be(PipelineRunState.Running);

        var act = () => ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        await act.Should().ThrowAsync<HttpRequestException>();

        var next = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        next.State.Should().Be(PipelineRunState.Passed);
    }

    [Fact]
    public async Task WhenHangEnqueued_ShouldObserveCancellation()
    {
        var ci = Fixture.FakeProviders.PipelineProvider;
        ci.EnqueueHang();

        var running = await ci.GetRunStatusAsync("feature/auto-1-x", "sha-1", CancellationToken.None);
        running.State.Should().Be(PipelineRunState.Running);
        running.Jobs.Should().ContainSingle();
        running.Jobs[0].Name.Should().Be("build-and-test");

        using var cts = new CancellationTokenSource();
        var waiting = ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), cts.Token);

        // Still pending before the token fires: a fake that threw at once, ignoring the token, would fail here.
        waiting.IsCompleted.Should().BeFalse("a hanging round waits until its token is cancelled");

        cts.CancelAfter(TimeSpan.FromMilliseconds(100));
        var act = () => waiting;
        await act.Should().ThrowAsync<OperationCanceledException>();

        // The hanging round was consumed by that wait, so the queue is back to the default.
        var next = await ci.WaitForCompletionAsync("feature/auto-1-x", "sha-1", TimeSpan.FromMinutes(1), CancellationToken.None);
        next.State.Should().Be(PipelineRunState.Passed);
    }

    // ── The real worker with a pipeline provider ────────────────────────────────────

    /// <summary>
    /// A real agent worker runs an <c>agent:next</c> issue with a pipeline provider wired in, and the
    /// external CI path polls the fake: once for the branch and head commit the run pushed.
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item>The work item ends <c>Succeeded</c>.</item>
    ///   <item><see cref="InMemoryPipelineProvider.ValidateCalls"/> is at least 1: the worker resolved the provider.</item>
    ///   <item>The first wait names the run's <c>feature/auto-5301-</c> branch and the fake repository's head SHA.</item>
    ///   <item>The issue ends with <c>agent:done</c>.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task WhenCiPasses_ShouldPollProviderForRunBranchAndSucceed()
    {
        const string issueId = "5301";
        var ci = Fixture.FakeProviders.PipelineProvider;

        // ── Arrange ────────────────────────────────────────────────────────────────

        // The config half of the wiring: the enrichment adds this to the assignment's ProviderConfigs only when
        // the store has it. The store's Reset() removes it again before the next test.
        await Fixture.ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "pipeline-e2e", Kind = ProviderKind.Pipeline, ProviderType = "GitHub", DisplayName = "E2E CI" },
            CancellationToken.None);

        // The post-PR CI wait first sleeps one poll interval; at the 30 s default the run would overrun the wait below.
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { ExternalCiPollInterval = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);

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
            Id = "scripted-ci-template",
            Name = "Scripted CI Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            PipelineProviderId = "pipeline-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "scripted-ci-profile",
            DisplayName = "Scripted CI Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        // Same agent scripts as Scenario A of RealAgentWorkerSmokeTests. The CI queue stays empty, so every
        // round is the fake's default pass.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()  // analysis agent
            .EnqueueReadyAnalysis()  // adversarial review agent
            .EnqueueCodeGen()        // code generation agent
            .EnqueueCodeGen()        // pre-PR cleanup agent
            .EnqueueFile(AgentWorkspacePaths.PrDescriptionFilePath, "### Summary\nScripted description")  // PR description agent
            .EnqueueCodeGen();       // feedback agent

        Fixture.QualityGateValidator.AlwaysPass();

        var agentId = $"scripted-ci-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(30));

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, timeout: TimeSpan.FromSeconds(10));

        // Resolving the provider validates it. Zero here means the worker never got a pipeline provider.
        ci.ValidateCalls.Should().BeGreaterThanOrEqualTo(1);

        ci.WaitCalls.Should().NotBeEmpty("the pipeline must wait for external CI when a pipeline provider is wired");
        ci.WaitCalls[0].Branch.Should().StartWith("feature/auto-5301-");
        ci.WaitCalls[0].CommitSha.Should().Be("abc123def456");

        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issue.Labels.Should().Contain(AgentLabels.Done);
    }
}
