using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Provider failures and a failed pre-flight on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process
/// against the E2E API host, with the fake providers injected as in <see cref="RealAgentWorkerSmokeTests"/>.
///
/// <para>
/// <c>AgentPhaseExecutorProviderOutageTests</c> and <c>AgentPhaseExecutorCodeGenTests</c> cover these outcomes with mocks,
/// so nothing there reaches the API. Here the worker runs the phase and reports over HTTP and the hub, and the tests read
/// what the API did with it: the WorkItem, the issue's label, the comments and the history. A provider outage must end as
/// an infrastructure failure with <c>agent:error</c>, never as <c>agent:needs-refinement</c>, which would ask the issue
/// author to rewrite an issue that is not at fault.
/// </para>
///
/// <para>
/// The pre-flight is the work-item pod's path, not a <c>JobRejected</c> message: a throw from the agent provider's
/// <c>ValidateAsync</c> leaves <c>AgentProviderResolver</c> and <c>LocalPipelineExecutor</c>, and <c>AgentJobRunner</c>
/// turns it into a Failed payload that the worker reports over HTTP.
/// </para>
///
/// <para>
/// The harness hands the worker the fake provider factory directly, so <c>ProviderFailureRecordingAgentProvider</c> never
/// wraps the scripted agent and the payload carries no <c>ProviderFailure</c>. In production, a
/// <c>PermanentAuthFailure</c> or <c>ProviderQuotaExhausted</c> result is recorded, the API pauses the credential and queues
/// the work again, and the issue goes back to its queue label (decisions.md, "A paused credential holds work"). What these
/// tests pin for those two categories is the infrastructure-failure path without that recording, not the production outcome.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerProviderFailureTests : HeadlessE2ETestBase
{
    public RealWorkerProviderFailureTests(E2EFixture fixture) : base(fixture) { }

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

    // The API thread swaps labels by replacing list elements while the label wait polls. ToArray copies without
    // enumerating, so the poll cannot fail with "Collection was modified" (a LINQ read of the live list can).
    private IReadOnlyList<string> AgentLabelsOn(string id) =>
        Fixture.IssueProvider.Issues.ToArray().Single(i => i.Identifier == id)
            .Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();

    /// <summary>
    /// Waits until the label swap has landed: a terminal label is on the issue and <c>agent:in-progress</c> is gone (a swap
    /// adds the new label before it removes the old). It does not wait for a particular label, so a wrong label fails the
    /// caller's assertion with the labels it found, not with a timeout. The Failed label comes from the API's HTTP
    /// handler, which can still be running when the WorkItem already reads Failed.
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForTerminalAgentLabelAsync(string id)
    {
        await WaitUntilAsync(() =>
        {
            var labels = AgentLabelsOn(id);
            return labels.Any(l => AgentLabels.TerminalLabels.Contains(l)) && !labels.Contains(AgentLabels.InProgress);
        });
        return AgentLabelsOn(id);
    }

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
    /// reports before it stops, so on return the report has been handled by the API.
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

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return System.Text.Json.JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    // ── analysis ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis agent exits 1 with a provider category that fails every later call too. The phase does not retry the
    /// analysis and does not ask the author to refine the issue: the run ends as an infrastructure failure, the issue ends
    /// with exactly <c>agent:error</c>, and the WorkItem carries the provider's own reason. Nothing posts a gate
    /// rejection, and no pull request is created.
    ///
    /// <para>
    /// <c>AttemptCount</c> is not asserted: the baseline runs before the analysis and calls the quality gate validator.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(AgentErrorCategory.ProviderQuotaExhausted, "6301")]
    [InlineData(AgentErrorCategory.PermanentAuthFailure, "6302")]
    public async Task WhenAnalysisHitsProviderFailure_ShouldEndInfrastructureFailureNotNeedsRefinement(
        AgentErrorCategory category, string issueId)
    {
        await SeedIssueAsync(issueId, "The provider fails during the analysis", $"real-provider-analysis-{issueId}-template");

        Fixture.AgentProvider.Enqueue(new CodeGenScript
        {
            ExitCode = 1,
            ErrorCategory = category,
            OutputLines = ["provider said no"]
        });

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        // First: a run that ended as needs-refinement shows that label here instead of timing out.
        (await WaitForTerminalAgentLabelAsync(issueId)).Should().Equal(AgentLabels.Error);

        entity.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
        entity.ErrorMessage.Should().Be($"Agent provider unavailable ({category}), exit code 1: provider said no");

        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FailureCategory.Should().Be(FailureReason.InfrastructureFailure);
        payload.FinalLabel.Should().BeNull("the phase does not swap a label; the API applies agent:error");

        Fixture.AgentProvider.Calls.Should().HaveCount(1, "the analysis is not retried when the provider is at fault");

        Fixture.IssueProvider.PostedComments
            .Where(c => c.Identifier == issueId)
            .Should().NotContain(c => c.Body.Contains(CommentMarkers.GateRejection));

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline stops before any code is written");
    }

    // ── code generation ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis and its review succeed, then code generation exits 1 with <c>PermanentAuthFailure</c>. Every fix
    /// attempt would fail the same way, so the run stops before the quality gates: the validator is never called, no pull
    /// request is created, and the item ends Failed as an infrastructure failure with <c>agent:error</c>.
    ///
    /// <para>
    /// The baseline check is switched off: it calls the validator during the analysis, and <c>AttemptCount</c> must show
    /// that the quality gates did not run.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCodegenHitsPermanentAuthFailure_ShouldStopBeforeQualityGates()
    {
        const string issueId = "6303";
        await SeedIssueAsync(issueId, "The provider rejects the login in code generation", "real-provider-codegen-auth-template");

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(c => c with { BaselineHealthCheckEnabled = false }, CancellationToken.None);

        // The analysis agent, then the adversarial review agent, then code generation.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new CodeGenScript { ExitCode = 1, ErrorCategory = AgentErrorCategory.PermanentAuthFailure });

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed);

        (await WaitForTerminalAgentLabelAsync(issueId)).Should().Equal(AgentLabels.Error);

        entity.FailureReason.Should().Be(FailureReason.InfrastructureFailure);

        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FailureCategory.Should().Be(FailureReason.InfrastructureFailure);
        payload.FinalLabel.Should().BeNull("the phase does not swap a label; the API applies agent:error");

        Fixture.QualityGateValidator.AttemptCount.Should().Be(0, "a permanent provider failure ends the run before the quality gates");

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline stops before the quality gates");
    }

    /// <summary>
    /// The analysis and its review succeed, and code generation keeps answering with a rate limit. A rate limit is not
    /// permanent: once the agent call's own retries are used up, code generation sees the non-zero exit and goes on to the
    /// quality gates. They run twice (the first pass and the final pass after the cleanup), a pull request is created, and
    /// the issue ends with <c>agent:done</c>.
    ///
    /// <para>
    /// <c>AgentStallMonitor</c> sends a rate-limited call again after <c>TransientRetryDelay</c> (30 s by default), up to
    /// <c>MaxProviderRetries</c> times in a row, and only then returns the last result as it is. So the delay is set to
    /// zero, or the run would outlast the wait for the worker, and the rate limit is scripted once more than the retries,
    /// or the first retry would hit the empty queue, which answers exit 0, and code generation would never see the failure.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenCodegenHitsRateLimit_ShouldContinueToQualityGates()
    {
        const string issueId = "6304";
        await SeedIssueAsync(issueId, "The provider rate-limits code generation", "real-provider-codegen-rate-limit-template");

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BaselineHealthCheckEnabled = false, TransientRetryDelay = TimeSpan.Zero },
            CancellationToken.None);

        // The analysis agent, then the adversarial review agent, then every attempt at code generation.
        Fixture.AgentProvider.EnqueueReadyAnalysis().EnqueueReadyAnalysis();
        for (var attempt = 0; attempt <= AgentStallMonitor.MaxProviderRetries; attempt++)
            Fixture.AgentProvider.Enqueue(new CodeGenScript { ExitCode = 1, ErrorCategory = AgentErrorCategory.ProviderRateLimit });

        Fixture.QualityGateValidator.AlwaysPass();

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded);

        (await WaitForTerminalAgentLabelAsync(issueId)).Should().Equal(AgentLabels.Done);

        ReadPayload(entity.Result).FinalStep.Should().Be(PipelineStep.Completed);

        // An unscripted call answers exit 0, so a Succeeded run alone does not show that code generation saw the rate limit.
        // Every scripted call is recorded as Scripted, so this count is the two analysis calls plus every rate limit used up.
        Fixture.AgentProvider.Calls.Count(c => c.Scripted).Should().Be(2 + AgentStallMonitor.MaxProviderRetries + 1,
            "code generation used up all its rate-limited calls and then got the last one back unchanged");

        Fixture.QualityGateValidator.AttemptCount.Should().Be(2,
            "the first pass after code generation and the final pass after the cleanup");

        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "a rate limit does not stop the pipeline");
    }

    // ── pre-flight ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// The agent provider's <c>ValidateAsync</c> throws before the worker runs anything. The worker reports a Failed
    /// payload over HTTP, so the item ends Failed instead of staying stuck, with the exception's message, and the issue
    /// ends with exactly <c>agent:error</c>. The agent is never called, no pull request is created, and the history keeps
    /// a Failed row.
    ///
    /// <para>
    /// The wait for <c>Failed</c> is the proof that the item is not stuck. <c>FailureReason</c> is not asserted: the
    /// payload carries no category, so the persisted value is the resolver's default, not something this path decides.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenAgentPreflightValidationFails_ShouldFailItemWithoutRunningAgent()
    {
        const string issueId = "6305";
        await SeedIssueAsync(issueId, "The agent CLI is missing", "real-provider-preflight-template");

        Fixture.AgentProvider.ValidateException = new InvalidOperationException("agent cli missing");

        var workItemId = await RunWorkerToCompletionAsync(issueId);

        var entity = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, timeout: TimeSpan.FromSeconds(30));
        var history = await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Failed);

        entity.ErrorMessage.Should().Be("agent cli missing");

        (await WaitForTerminalAgentLabelAsync(issueId)).Should().Equal(AgentLabels.Error);

        var payload = ReadPayload(entity.Result);
        payload.FinalStep.Should().Be(PipelineStep.Failed);
        payload.FailureReason.Should().Be("agent cli missing");

        history.FinalStep.Should().Be(PipelineStep.Failed);

        Fixture.AgentProvider.Calls.Should().BeEmpty("the pre-flight fails before any agent call");

        Fixture.RepositoryProvider.MethodCalls.Should().NotContain(nameof(IRepositoryProvider.CreatePullRequestAsync),
            "the pipeline never starts");
    }
}
