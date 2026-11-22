using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// What the Run page shows for a run that the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> executed, in
/// the browser: the output tail of a finished run is the output the worker masked before it reported it.
///
/// <para>
/// The run is the same as in <c>RealWorkerEnvironmentTests.WhenAgentOutputContainsProjectSecret_WorkerShouldMaskItBeforeItLeaves</c>,
/// which asserts the API's live output and the history row; this class only looks at the page.
/// </para>
///
/// <para>
/// Issue #4151 also specified a second test, <c>WhenQualityGateFailed_RunPageShouldShowTheAttemptWhileFixAgentRuns</c>, that
/// expects the API's run to hold the failed quality gate report while the fix agent runs, and the Run page to show
/// "Attempt 1/" in <c>.qg-header-fail</c>. It is not included, because the product does not behave that way: the worker
/// reports a quality gate result to the hub only when it creates the pull request
/// (<c>AgentCallbacks.CreatePullRequest</c>), not after each gate pass. Run against that scenario, the wait for
/// <c>LatestQualityReport</c> timed out after 25 s with the run in <c>GeneratingCode</c>, the fix agent's call waiting, one
/// validator attempt made, <c>LatestQualityReport</c> still null and <c>QualityGateHistory</c> empty.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerRunPageReportsTests : E2ETestBase
{
    private const string SecretValue = "s3cr3t-e2e-value";
    private const string SecretLine = "token=" + SecretValue;
    private const string MaskedLine = "token=***";

    /// <summary>A project that is not the Default one, so that its secrets reach the assignment only through the payload.</summary>
    private static readonly Guid SecretsProjectId = Guid.Parse("22222222-3333-4444-5555-666666666666");

    public RealWorkerRunPageReportsTests(E2EFixture fixture) : base(fixture) { }

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
    ///
    /// <para>
    /// A copy of <c>RealAgentWorkerSmokeTests.CreateAndClaimWorkItemAsync</c>. <see cref="E2ETestBase"/> has no
    /// <c>InsertPendingWorkItemAsync</c>, so the row is added here with the fields that helper writes.
    /// <paramref name="projectId"/> goes on the row and on the payload: the enricher reads the project, and so its secrets,
    /// from the payload, and the run takes its project from the row.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        Guid? projectId = null,
        CancellationToken ct = default)
    {
        var workItemId = Guid.NewGuid();
        await using (var insertDb = Fixture.DbContextFactory.CreateDbContext())
        {
            insertDb.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = issueId,
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Pending,
                Payload = WorkItemTestData.RoutablePayload(WorkItemTaskType.Implementation, issueId, "issue-e2e", "repo-e2e"),
                AgentSelector = "e2e",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600,
                ProjectId = projectId ?? Guid.Parse(WellKnownIds.DefaultProjectId)
            });
            await insertDb.SaveChangesAsync(ct);
        }

        // A minimal-but-valid payload so the API's GetAssignment endpoint can deserialize it (JobDistributionRequest has
        // required properties that fail with "{}"). The item stays Pending until it is routed below.
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync([workItemId], ct)
            ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");

        // Build the identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment
        // enriches it with configs. LocalPipelineExecutor's ProviderFactoryOverride still bypasses the
        // provider config lookup because fakeProviders is injected.
        var minimalPayload = new JobDistributionRequest
        {
            IssueIdentifier = issueId,
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-smoke-test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Implementation,
            ProjectId = projectId,
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

    /// <summary>Seeds an <c>agent:next</c> issue and the one enabled template that owns <c>repo-e2e</c>, in <paramref name="projectId"/>.</summary>
    private async Task SeedIssueAndTemplateAsync(string issueId, string templateId, string projectId)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Goal\nDo something.",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(projectId, new PipelineJobTemplate
        {
            Id = templateId,
            Name = templateId,
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);
    }

    /// <summary>A snapshot of the output lines the API's run holds for the work item; empty until the run exists.</summary>
    private string[] OutputLinesOf(Guid workItemId) =>
        Fixture.RunService.GetRun(new RunId(workItemId.ToString()))?.OutputLines.ToArray() ?? [];

    /// <summary>Stands in for the review call: the next agent call waits on <paramref name="pause"/>.</summary>
    private sealed class SetPauseScript(ScriptedAgentProvider provider, TaskCompletionSource pause) : AgentScript
    {
        public override void Execute(string workspacePath) => provider.PauseBeforeExecution = pause;
    }

    // ── Output tail ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// The analysis agent prints a line that carries a project secret, and the run finishes. The Run page's output tail card
    /// shows the masked line and the page text has no trace of the secret.
    ///
    /// <para>
    /// The run is held in code generation until the masked line has landed on the API's run: the worker sends its output in
    /// batches every 250 ms, and the batch that is still open when the run ends is lost. Without the pause the line could
    /// miss the tail the page shows.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenRunFinished_RunPageOutputTailShouldShowMaskedSecret()
    {
        const string issueId = "6505";

        var pause = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new RealAgentWorkerHarness();
        try
        {
            // ── Arrange ────────────────────────────────────────────────────────────────

            // The payload's project decides whether the worker gets the secret, so the project is saved before the claim.
            await Fixture.ConfigStore.SaveProjectAsync(new PipelineProject
            {
                Id = SecretsProjectId.ToString(),
                Name = "Secrets Project",
                Enabled = true,
                TemplateIds = [],
                Secrets = new Dictionary<string, string> { ["E2E_TOKEN"] = SecretValue }
            }, CancellationToken.None);
            await SeedIssueAndTemplateAsync(issueId, "real-pages-secrets-template", SecretsProjectId.ToString());

            // Analysis (prints the secret), then the adversarial review call, which sets the pause: the next call waits.
            Fixture.AgentProvider
                .Enqueue(new AnalysisScript { OutputLines = [SecretLine] })
                .Enqueue(new SetPauseScript(Fixture.AgentProvider, pause));

            // ── Act ────────────────────────────────────────────────────────────────────

            var agentId = $"real-worker-pages-{Guid.NewGuid():N}";
            var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId, SecretsProjectId);

            await harness.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await WaitUntilAsync(() => OutputLinesOf(workItemId).Contains(MaskedLine));

            pause.TrySetResult();
            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            // The page reads history, and treats a run without a terminal step as live, so wait for the finished row first.
            await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed);

            // A run's id is its work item id.
            var runPage = new RunDetailPage(Page, BaseUrl);
            await runPage.NavigateAsync(workItemId.ToString());

            // ── Assert ─────────────────────────────────────────────────────────────────

            // The tail first: it proves the card rendered, so the absence of the secret below is not vacuous.
            (await runPage.GetOutputTailLinesAsync()).Should().Contain(l => l.Contains(MaskedLine, StringComparison.Ordinal),
                "the output tail card shows the line the worker masked");
            (await runPage.GetPageTextAsync()).Should().NotContain(SecretValue,
                "the secret appears nowhere on the page");
        }
        finally
        {
            pause.TrySetResult();
            await harness.DisposeAsync();
        }
    }
}
