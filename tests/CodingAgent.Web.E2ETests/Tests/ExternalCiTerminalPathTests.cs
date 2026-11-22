using System.Text.Json;
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
/// Three external CI paths that end a run early or without the fix agent, on the real
/// <see cref="CodingAgent.Agent.WorkItemAgentService"/> with a scripted <see cref="InMemoryPipelineProvider"/>:
/// <list type="bullet">
///   <item>A CI failure whose log shows an infrastructure problem is retried without charging the agent (#3911).</item>
///   <item>A PR that is conflicted, merged or closed while the worker polls CI ends the run cleanly, with the right label.</item>
///   <item>A push refused because of workflow files gives the fix agent one chance, then stops the loop and leaves a
///         draft PR (#3910).</item>
/// </list>
///
/// <para>
/// The conflict and PR-state checks run only after CI did not appear within <c>CiNotStartedTimeout</c>. A round with a
/// status completes the poll without reaching them, so the CI5 tests script <c>EnqueueNotStarted()</c> and a short
/// timeout. The PR number in every test is 1, because the fake repository's <c>CreatePullRequestAsync</c> always
/// returns <c>.../pull/1</c> and the draft PR is created before the first poll.
/// </para>
///
/// <para>
/// Every test uses its own issue number, because the harness runs all workers under one workspace root.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class ExternalCiTerminalPathTests : HeadlessE2ETestBase
{
    /// <summary>What GitHub answers when the pipeline's App pushes a change to <c>.github/workflows</c>.</summary>
    private const string Refusal =
        "refusing to allow a GitHub App to create or update workflow: GitHub App may not create or update workflow files";

    public ExternalCiTerminalPathTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an Implementation work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. Same as the helper in
    /// <c>ScriptedCiSmokeTests</c>: the payload names the pipeline provider, which
    /// <c>HeadlessE2ETestBase.DispatchIssueAsync</c> cannot do. Returns the claimed work item GUID.
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(issueId, agentSelector: "e2e");

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it. The item stays Pending
        // until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        entity.TaskType = WorkItemTaskType.Implementation;

        // The identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment enriches it with
        // configs. PipelineProviderConfigId is one half of the pipeline provider wiring: the enrichment copies it to the
        // assignment, and adds the matching ProviderConfig from the store.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            PipelineProviderConfigId = "pipeline-e2e",
            InitiatedBy = "e2e-external-ci-terminal-path",
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
            Id = "profile-e2e-external-ci-terminal-path",
            DisplayName = "External CI terminal path profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });
        entity.Payload = JsonSerializer.Serialize(minimalPayload, PipelineJsonOptions.Default);

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
    /// The arrange every test shares: the pipeline provider's config and template (without both, the run skips external
    /// CI silently), an <c>agent:next</c> issue, the agent scripts and passing quality gates.
    /// </summary>
    private async Task ArrangeRunAsync(string issueId)
    {
        await Fixture.ConfigStore.SaveProviderConfigAsync(
            new ProviderConfig { Id = "pipeline-e2e", Kind = ProviderKind.Pipeline, ProviderType = "GitHub", DisplayName = "E2E CI" },
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
            Id = "external-ci-terminal-path-template",
            Name = "External CI terminal path template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            PipelineProviderId = "pipeline-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Analysis, adversarial analysis review, code generation. Later calls (a fix agent, cleanup, PR description,
        // feedback) are unscripted and return exit 0.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        Fixture.QualityGateValidator.AlwaysPass();
    }

    /// <summary>
    /// Runs the real worker on the claimed item for <paramref name="issueId"/> until it stops, then waits for the work
    /// item to reach <paramref name="expected"/> and returns the row with the completion payload the worker reported.
    /// <paramref name="config"/> changes the pipeline configuration first. The post-PR CI wait sleeps one
    /// <c>ExternalCiPollInterval</c>, which at the 30 s default would overrun the worker wait, so it is always 50 ms.
    /// </summary>
    private async Task<(WorkItemEntity Item, JobCompletionPayload Payload)> RunWorkerAsync(
        string issueId,
        WorkItemStatus expected,
        Func<PipelineConfiguration, PipelineConfiguration>? config = null)
    {
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => (config is null ? c : config(c)) with { ExternalCiPollInterval = TimeSpan.FromMilliseconds(50) },
            CancellationToken.None);

        var agentId = $"external-ci-terminal-path-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        // The worker awaits its HTTP and hub reports before it stops, so the label swap is under way when this returns.
        await harness.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(30));

        var item = await WaitForWorkItemStatusAsync(workItemId, expected, timeout: TimeSpan.FromSeconds(10));

        item.Result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return (item, JsonSerializer.Deserialize<JobCompletionPayload>(item.Result!, PipelineJsonOptions.Default)!);
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
    /// Waits until the pipeline has recorded an add of <paramref name="label"/> on <paramref name="issueId"/>. Use it
    /// when the issue already carries the label, so the labels alone cannot show that the pipeline wrote it.
    /// </summary>
    private async Task AssertLabelAddedAsync(string issueId, string label)
    {
        bool Added() => Fixture.IssueProvider.LabelChanges.ToArray()
            .Any(c => c.Identifier == issueId && c.Label == label && c.Added);

        try
        {
            await WaitUntilAsync(Added, TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The assertion below reports the label changes that were recorded.
        }

        Added().Should().BeTrue(
            "the pipeline adds {0} to issue {1}. Recorded changes: {2}",
            label,
            issueId,
            string.Join(", ", Fixture.IssueProvider.LabelChanges.ToArray().Select(c => $"{(c.Added ? "+" : "-")}{c.Label}")));
    }

    /// <summary>
    /// A short wait for CI to appear and one retry, so a run whose CI never appears reaches the conflict and PR-state
    /// checks after about 200 ms.
    /// </summary>
    private static PipelineConfiguration WithShortCiNotStartedWait(PipelineConfiguration c) =>
        c with { CiNotStartedTimeout = TimeSpan.FromMilliseconds(200), CiNotStartedMaxRetries = 1 };

    // ── CI2: infrastructure failure in the CI log (#3911) ───────────────────────────

    /// <summary>
    /// CI fails once with a log that shows an infrastructure problem, then passes. The pipeline classifies the log as
    /// infrastructure, pushes an empty commit and polls again without the fix agent, so the run ends <c>Succeeded</c>
    /// with <c>RetryCount</c> 0, and CI was waited on at least twice.
    /// </summary>
    [Theory]
    [InlineData("7501", "E: Unable to fetch some archives, maybe run apt-get update")]
    [InlineData("7502", "Temporary failure in name resolution")]
    public async Task WhenCiLogShowsInfrastructureFailure_ShouldRetryWithoutChargingTheAgent(string issueId, string log)
    {
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeRunAsync(issueId);
        ci.EnqueueFailure("unit", 42, log).EnqueuePass();

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        payload.RetryCount.Should().Be(0, "an infrastructure failure is retried without the fix agent");
        ci.WaitCalls.Count.Should().BeGreaterThanOrEqualTo(2, "the failed round and the retry were both waited on");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// The control: a log with a compiler error and an infrastructure phrase is a code failure, because the code
    /// patterns win. The fix agent runs once, so the run ends <c>Succeeded</c> with <c>RetryCount</c> 1.
    /// </summary>
    [Fact]
    public async Task WhenCiLogShowsCodeAndInfrastructureFailure_ShouldChargeTheAgent()
    {
        const string issueId = "7503";
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeRunAsync(issueId);
        ci.EnqueueFailure("unit", 42, "error CS1002: ; expected\nTemporary failure in name resolution").EnqueuePass();

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        payload.RetryCount.Should().Be(1, "a code failure goes to the fix agent even when the log also shows an infrastructure phrase");
    }

    // ── CI5: the PR ends while the worker polls CI ──────────────────────────────────

    /// <summary>
    /// CI never appears and the PR is conflicted. The run ends <c>ConflictRestart</c>, which reports <c>Succeeded</c>
    /// with no retry used, and the issue goes back to <c>agent:next</c> so a new run can start from the base branch.
    /// </summary>
    [Fact]
    public async Task WhenPrBecomesConflictedDuringPoll_ShouldEndWithConflictRestartAndRequeue()
    {
        const string issueId = "7504";
        await ArrangeRunAsync(issueId);
        Fixture.FakeProviders.PipelineProvider.EnqueueNotStarted();
        Fixture.RepositoryProvider.PrMergeability[1] = PrMergeabilityStatus.Conflicted;

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded, WithShortCiNotStartedWait);

        payload.FinalStep.Should().Be(PipelineStep.ConflictRestart);
        payload.RetryCount.Should().Be(0);
        await AssertAgentLabelsAsync(issueId, AgentLabels.Next);

        // The issue is seeded with agent:next, so the label check above also holds if the pipeline writes nothing. The
        // requeue is the pipeline adding agent:next at the end of the run.
        await AssertLabelAddedAsync(issueId, AgentLabels.Next);
    }

    /// <summary>
    /// CI never appears and the PR was merged. The run ends <c>PrMerged</c>, which reports <c>Succeeded</c> with no
    /// retry used and <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenPrIsMergedDuringPoll_ShouldEndCleanlyWithDoneLabel()
    {
        const string issueId = "7505";
        await ArrangeRunAsync(issueId);
        Fixture.FakeProviders.PipelineProvider.EnqueueNotStarted();
        Fixture.RepositoryProvider.PrStates[1] = PullRequestState.Merged;

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded, WithShortCiNotStartedWait);

        payload.FinalStep.Should().Be(PipelineStep.PrMerged);
        payload.RetryCount.Should().Be(0);
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// CI never appears and the PR was closed without merging. The run ends <c>PrClosed</c>, which reports
    /// <c>Cancelled</c> with <c>agent:cancelled</c>. The run's history row may show <c>Cancelled</c> as its last step,
    /// so only the worker's reported <c>PrClosed</c> is asserted.
    /// </summary>
    [Fact]
    public async Task WhenPrIsClosedDuringPoll_ShouldEndCancelled()
    {
        const string issueId = "7506";
        await ArrangeRunAsync(issueId);
        Fixture.FakeProviders.PipelineProvider.EnqueueNotStarted();
        Fixture.RepositoryProvider.PrStates[1] = PullRequestState.Closed;

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Cancelled, WithShortCiNotStartedWait);

        payload.FinalStep.Should().Be(PipelineStep.PrClosed);
        await AssertAgentLabelsAsync(issueId, AgentLabels.Cancelled);
    }

    // ── G4: a push refused because of workflow files (#3910) ────────────────────────

    /// <summary>
    /// The first push is refused. The refusal is a plain gate failure, so the fix agent runs once and the second push
    /// succeeds. CI passes (the queue is empty), so the run ends <c>Succeeded</c> with <c>RetryCount</c> 1.
    /// </summary>
    [Fact]
    public async Task WhenWorkflowPushIsRefusedOnce_FixAgentShouldUseOneRetryThenSucceed()
    {
        const string issueId = "7507";
        await ArrangeRunAsync(issueId);
        Fixture.RepositoryProvider.PushErrors.Enqueue(new InvalidOperationException(Refusal));

        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Succeeded);

        payload.RetryCount.Should().Be(1, "the first refusal gives the fix agent one chance");
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }

    /// <summary>
    /// Both the push and the push after the fix agent are refused. The second refusal is an infrastructure failure, so
    /// the loop stops without a second fix agent although <c>MaxRetries</c> is 3, and the run ends <c>Failed</c> with a
    /// draft PR and <c>agent:error</c>.
    ///
    /// <para>
    /// Both pushes fail before the first CI poll, so CI is never waited on. The draft finalization pushes a third time;
    /// the queue is empty by then, so that push succeeds. <c>FailureReason</c> and <c>FailureCategory</c> are not
    /// asserted: <c>FinalizeDraftPrAsync</c> sets the category without regard for the infrastructure gate that stopped
    /// the loop.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenWorkflowPushIsRefusedTwice_ShouldStopTheLoopAndLeaveDraftPr()
    {
        const string issueId = "7508";
        var ci = Fixture.FakeProviders.PipelineProvider;
        await ArrangeRunAsync(issueId);
        Fixture.RepositoryProvider.PushErrors.Enqueue(new InvalidOperationException(Refusal));
        Fixture.RepositoryProvider.PushErrors.Enqueue(new InvalidOperationException(Refusal));

        // TODO [WARNING] This test cannot fail on RetryCount under the mutation the acceptance criteria name. If
        // BuildWorkflowPushRefusedGate always returns errorGate, the second refusal gets a second fix agent, the third push
        // succeeds, and the run ends Succeeded with RetryCount 2. RunWorkerAsync then throws a 10 s TimeoutException waiting
        // for Failed, so the RetryCount assertion below is never reached. A helper that returns the item once it is
        // terminal in any status would let the RetryCount and IsDraftPr assertions run and name the observed values.
        var (_, payload) = await RunWorkerAsync(issueId, WorkItemStatus.Failed);

        payload.RetryCount.Should().Be(1, "the first refusal used one fix agent and the second stopped the loop, although MaxRetries is 3");
        payload.IsDraftPr.Should().BeTrue();
        ci.WaitCalls.Should().BeEmpty("both pushes failed before any poll");
        Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CreatePullRequestAsync));
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }
}
