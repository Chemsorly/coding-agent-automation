using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.E2ETests.PageObjects;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// What the cockpit shows for a run that the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> ended at the
/// analysis gate with <c>wont_do</c>: the run is Completed, not Failed, so it is a Completed row on the Runs page and
/// appears in no Attention list, and its run page says "Won't do" on the analysis step.
///
/// <para>
/// The run is the same as in <c>RealWorkerAnalysisGateTests</c>, which asserts its labels, payload and comments; this
/// class only looks at the pages.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerWontDoPagesTests : E2ETestBase
{
    public RealWorkerWontDoPagesTests(E2EFixture fixture) : base(fixture) { }

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
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
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
                ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId)
            });
            await insertDb.SaveChangesAsync(ct);
        }

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

    // ── Won't do ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A real worker ends an issue at the analysis gate with <c>wont_do</c>. The run is Completed: it is a Completed row
    /// on the Runs page with the Completed badge, no Attention section lists it (the aggregator only knows
    /// <c>NotReady</c> and runs that Failed), and its run page shows "Won't do" on the analysis step.
    /// </summary>
    [Fact]
    public async Task WhenRunEndsWontDo_PagesShowCompletedWontDoAndNoAttentionRow()
    {
        const string issueId = "6104";

        // ── Arrange ────────────────────────────────────────────────────────────────

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add something that already exists",
            Description = "## Goal\nDo something.",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-gate-pages-template",
            Name = "Real Gate Pages Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-gate-pages-profile",
            DisplayName = "Real Gate Pages Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        // First script: the analysis agent. Second script: the adversarial review agent, which writes the assessment again.
        Fixture.AgentProvider
            .EnqueueAnalysis("wont_do", "Already implemented")
            .EnqueueAnalysis("wont_do", "Already implemented");

        Fixture.QualityGateValidator.AlwaysPass();

        // ── Act ────────────────────────────────────────────────────────────────────

        var agentId = $"real-worker-pages-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await using (var harness = new RealAgentWorkerHarness())
        {
            await harness.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
        }

        // The pages read history; the Runs page loads once, so wait for the row before navigating.
        await WaitForHistoryAsync(r => r.IssueIdentifier == issueId && r.FinalStep == PipelineStep.Completed);

        // ── Assert: Runs page ──────────────────────────────────────────────────────

        var runsPage = new RunsPage(Page, BaseUrl);
        await runsPage.NavigateAsync();
        await runsPage.SelectTabAsync("Completed");
        await WaitUntilAsync(async () => await runsPage.IsRunVisibleAsync(issueId));
        (await runsPage.IsRunVisibleAsync(issueId)).Should().BeTrue("a won't-do run is Completed, so it is on the Completed tab");
        (await runsPage.GetBadgeLabelForRunAsync(issueId)).Should().Be(RunOutcomeDisplay.Label(PipelineStep.Completed));

        // ── Assert: Attention page ─────────────────────────────────────────────────

        var attentionPage = new AttentionPage(Page, BaseUrl);
        await attentionPage.NavigateAsync();
        (await attentionPage.IsRowVisibleAsync(issueId)).Should().BeFalse(
            "Attention lists NotReady runs and Failed runs, and a won't-do run is neither");

        // ── Assert: run page ───────────────────────────────────────────────────────

        // A run's id is its work item id.
        await new RunDetailPage(Page, BaseUrl).NavigateAsync(workItemId.ToString());
        var analysisStep = Page.Locator("[data-testid='pipeline-step-AnalyzingCode']");
        await analysisStep.WaitForAsync();
        (await analysisStep.InnerTextAsync()).Should().Contain("Won't do");
    }
}
