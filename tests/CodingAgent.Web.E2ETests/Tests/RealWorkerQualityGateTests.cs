using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The quality gate fix loop, its exhaustion and the code generation failure paths on the real
/// <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API host, with the fake providers
/// injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// <c>QualityGateRetryTests</c> drives these outcomes with a <c>FakeAgentClient</c> that reports the steps and the retry
/// count by hand, so <c>QualityGateExecutor</c> never runs. Here the worker runs the gates through
/// <see cref="ConfigurableQualityGateValidator"/>, the fix agent is a scripted call, and the outcome is reported over HTTP
/// and the hub.
/// </para>
///
/// <para>
/// Every test starts by switching the workspace baseline off. <c>VerifyBaselineStep</c> calls the same validator, so with
/// the baseline on it would consume the first validator attempt: <c>FailThenPass(1)</c> would fail only the baseline, which
/// never fails the run, and no fix agent would ever run.
/// </para>
///
/// <para>
/// The validator counts below also rely on <c>InMemoryRepositoryProvider</c> reporting no working-tree fingerprint. The final
/// gate pass after the cleanup agent is skipped only when the tree is known to be unchanged, and an unknown tree counts as
/// changed. If the fake ever reports a fingerprint, the counts of the runs that reach the cleanup drop by one.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
///
/// <para>
/// Issue #4148 also specified a browser test, <c>WhenCodegenFails_RunPageShouldShowTheReason</c>, that expects the Run
/// page to show a codegen failure's reason in <c>.summary-failure-callout</c>. It is not included, because the product
/// does not behave that way: <c>RunPage.razor</c> renders the callout only when the run has no diagnosis, and a codegen
/// failure ends <c>Failed</c> with the category <c>AgentError</c>, which <c>RunDiagnoser</c> always diagnoses. Run against
/// this scenario, the page had no <c>.summary-failure-callout</c> and showed the diagnosis card (verdict "Unknown", title
/// "No rule matched this failure") with the reason in its evidence line. The reason is therefore asserted here, from the
/// work item's <c>ErrorMessage</c>, and not through the page.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerQualityGateTests : HeadlessE2ETestBase
{
    public RealWorkerQualityGateTests(E2EFixture fixture) : base(fixture) { }

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

    /// <summary>The prompt of every agent call the worker made, in call order.</summary>
    private List<string> RecordedPrompts() =>
        Fixture.AgentProvider.Calls.Select(c => c.Request.Prompt).ToList();

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    // ── Quality gate fix loop ───────────────────────────────────────────────────────

    /// <summary>
    /// The first gate pass fails and the fix agent's change passes. The run ends Completed with a ready pull request, one
    /// fix call was made, and no failure feedback was collected.
    ///
    /// <para>
    /// Validator calls: the initial fail, the pass after the fix, and the final pass after the cleanup agent. The agent
    /// calls are analysis, analysis review, code generation, fix, cleanup, PR description and success feedback; only the
    /// first three are scripted.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenQualityGateFailsOnce_ShouldRunFixAgentAndFinishWithReadyPullRequest()
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        const string issueId = "6201";
        await SeedIssueAsync(issueId, "Add input validation", "real-qg-retry-template");

        // Analysis, adversarial analysis review, code generation. Later calls are unscripted and return exit 0.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();
        Fixture.QualityGateValidator.FailThenPass(1);

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.Done);

        // Initial fail, pass after the fix, final pass after the cleanup agent.
        Fixture.QualityGateValidator.AttemptCount.Should().Be(3);

        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Completed);
        payload.RetryCount.Should().Be(1);
        payload.IsDraftPr.Should().BeFalse();
        payload.PullRequestUrl.Should().NotBeNull();

        var prompts = RecordedPrompts();
        prompts.Count(p => p.Contains("Quality gates failed (attempt 1/3):", StringComparison.Ordinal)).Should().Be(1,
            "one failed gate pass means one fix prompt");
        prompts.Should().NotContain(p => p.Contains("# Pipeline Failure Feedback", StringComparison.Ordinal),
            "failure feedback is collected only when the retries are exhausted");
    }

    /// <summary>
    /// Every gate pass fails. The fix loop uses all 3 retries, one extra feedback call collects the failure (it does not
    /// count against <c>MaxRetries</c>), and the run ends Failed with a draft pull request and <c>agent:error</c>.
    ///
    /// <para>
    /// Validator calls: the initial fail and one after each of the 3 fixes. Exhaustion goes straight to the draft
    /// finalization, so there is no cleanup agent and no final pass.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenQualityGateAlwaysFails_ShouldEndFailedWithDraftPullRequestAndOneFeedbackCall()
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        const string issueId = "6202";
        await SeedIssueAsync(issueId, "Add input validation", "real-qg-exhausted-template");

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();
        Fixture.QualityGateValidator.AlwaysFail("Tests failed");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        entity.FailureReason.Should().Be(FailureReason.QualityGateExhausted);
        entity.ErrorMessage.Should().Be("Quality gates failed after max retries; draft PR created.");

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.Error);

        // The initial fail plus one pass after each of the 3 fixes.
        Fixture.QualityGateValidator.AttemptCount.Should().Be(4);

        // On the Failed path the API writes the history row before the hub report arrives, so the draft and retry fields
        // are read from the WorkItem's Result.
        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FinalLabel.Should().Be(AgentLabels.Error);
        payload.IsDraftPr.Should().BeTrue();
        payload.RetryCount.Should().Be(3);
        payload.PullRequestUrl.Should().NotBeNull();

        var prompts = RecordedPrompts();
        prompts.Count(p => p.Contains("# Pipeline Failure Feedback", StringComparison.Ordinal)).Should().Be(1,
            "the exhausted run makes one dedicated feedback call");
        prompts.Count(p =>
                p.Contains("Quality gates failed (attempt ", StringComparison.Ordinal)
                && !p.Contains("# Pipeline Failure Feedback", StringComparison.Ordinal))
            .Should().Be(3, "the feedback call does not count against MaxRetries");

        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the exhausted run leaves its work in a draft pull request");
    }

    // ── Code generation failures ────────────────────────────────────────────────────

    /// <summary>
    /// The code generation agent throws and the workspace has no file changes. The run fails at once with the exception's
    /// message, the quality gates never run, and no pull request is created.
    ///
    /// <para>
    /// <c>FailureReason</c> is not asserted: <c>FailPhaseAsync</c> sets no failure category, so the persisted value is the
    /// resolver's default, not something code generation decides.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCodegenThrowsWithoutChanges_ShouldFailWithoutPullRequest()
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        const string issueId = "6203";
        await SeedIssueAsync(issueId, "Add input validation", "real-qg-codegen-throws-template");

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new CodeGenScript { ThrowOnExecute = new InvalidOperationException("scripted codegen crash") });

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        entity.ErrorMessage.Should().Be("Code generation failed with no file changes: scripted codegen crash");

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.Error);

        Fixture.QualityGateValidator.AttemptCount.Should().Be(0, "the run fails before the quality gates");

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a run that fails in code generation opens no pull request");
    }

    /// <summary>
    /// The code generation agent exits with the timeout code (124). The run fails with the timeout message, the quality
    /// gates never run, and no pull request is created.
    ///
    /// <para>
    /// The duration in the message is the seeded <c>AgentTimeout</c> of 2 minutes. <c>FailureReason</c> is not asserted for
    /// the same reason as in <see cref="WhenCodegenThrowsWithoutChanges_ShouldFailWithoutPullRequest"/>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCodegenExitsWithTimeoutCode_ShouldFailWithoutPullRequest()
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        const string issueId = "6204";
        await SeedIssueAsync(issueId, "Add input validation", "real-qg-codegen-timeout-template");

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen(exitCode: 124);

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        entity.ErrorMessage.Should().Be("Agent timed out after 00:02:00. Implementation is incomplete.");

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.Error);

        Fixture.QualityGateValidator.AttemptCount.Should().Be(0, "the run fails before the quality gates");

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a run that fails in code generation opens no pull request");
    }

    /// <summary>
    /// The code generation agent exits non-zero (but not with the timeout code). The pipeline logs it and carries on to the
    /// quality gates, which pass, so the run ends Completed with a pull request.
    ///
    /// <para>
    /// Validator calls: the initial pass and the final pass after the cleanup agent.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCodegenExitsNonZero_ShouldContinueToQualityGates()
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        const string issueId = "6205";
        await SeedIssueAsync(issueId, "Add input validation", "real-qg-codegen-nonzero-template");

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen(exitCode: 1);
        Fixture.QualityGateValidator.AlwaysPass();

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        // Every call takes the next script in order, so the exit-1 script (the third one) belongs to the third call. Without
        // this, the test would also pass if that script went to some other call and code generation exited 0.
        var calls = Fixture.AgentProvider.Calls;
        calls.Should().HaveCountGreaterThanOrEqualTo(3);
        calls[2].Request.Prompt.Should().Contain(DefaultPrompts.Implementation,
            "the third call is code generation, which is the call the exit-1 script was queued for");

        AgentLabelsOn(issueId).Should().Equal(AgentLabels.Done);

        // Initial pass, final pass after the cleanup agent.
        Fixture.QualityGateValidator.AttemptCount.Should().Be(2);

        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a non-zero exit from code generation does not stop the run");
    }
}
