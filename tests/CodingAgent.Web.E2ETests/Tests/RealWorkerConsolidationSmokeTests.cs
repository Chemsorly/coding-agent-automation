using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke test that runs a refactoring scan through the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>
/// in-process, with the fake providers injected into <see cref="CodingAgent.Agent.LocalConsolidationExecutor"/>
/// as its <c>providerFactoryOverride</c> test seam.
///
/// <para>
/// Without the override the consolidation executor builds a real <c>AgentProviderFactory</c> and real GitHub
/// or GitLab issue providers from the job's provider configs, which the E2E host only seeds with empty
/// settings. Reverting the override in <see cref="RealAgentWorkerHarness"/> makes this test fail.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerConsolidationSmokeTests : HeadlessE2ETestBase
{
    private const string TemplateId = "scan-template";
    private const string ScanKey = "RefactoringDetection:" + TemplateId;

    public RealWorkerConsolidationSmokeTests(E2EFixture fixture) : base(fixture) { }

    /// <summary>
    /// A refactoring scan with one detector (Structure focus) and no adversarial review runs Phase 0, Phase 1,
    /// Phase 2a and the aggregation, in that order. The aggregation step writes one proposal; the worker creates
    /// the issue through the injected issue provider and reports the work item as succeeded.
    /// </summary>
    [Fact]
    public async Task WhenRefactoringScanProposesOneChange_ShouldCreateIssueThroughInjectedProvider()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = TemplateId,
            Name = "Scan Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        // Without the review, the queue needs no scripts for it.
        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { RefactoringReviewEnabled = false }, CancellationToken.None);

        // Structure focus has one detector, so each phase runs one agent and the script order is fixed:
        // Phase 0, Phase 1, Phase 2a, then the aggregation that writes the proposals file.
        Fixture.AgentProvider
            .EnqueueCodeGen()
            .EnqueueCodeGen()
            .EnqueueCodeGen()
            .Enqueue(new RefactoringProposalsScript
            {
                Proposals =
                [
                    new RefactoringProposal
                    {
                        Title = "Split the handler",
                        AffectedFiles = ["src/Handler.cs"],
                        Description = "Split it.",
                        Rationale = "Too long."
                    }
                ]
            });

        var agentId = $"real-worker-consolidation-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimConsolidationWorkItemAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        try
        {
            await using var harness = new RealAgentWorkerHarness();
            await harness.StartAsync(
                agentHubUrl: Fixture.AgentHubUrl,
                apiKey: Fixture.ApiKey,
                agentId: agentId,
                workItemId: workItemId.ToString(),
                fakeProviders: Fixture.FakeProviders,
                qualityGateValidator: Fixture.QualityGateValidator);

            await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));

            // ── Assert ─────────────────────────────────────────────────────────────

            await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

            Fixture.AgentProvider.UnscriptedCalls.Should().BeEmpty(
                "every agent call of the scan must be scripted; unscripted prompts: {0}",
                string.Join(" | ", Fixture.AgentProvider.UnscriptedCalls.Select(c => c.Request.Prompt.Split('\n')[0])));

            // A dropped phase would hand the aggregation a CodeGenScript, and the executor's one retry for a missing
            // proposals file would then consume the proposals script: still four calls, but the aggregation prompt
            // would appear twice. Four distinct prompts rule that out.
            var prompts = Fixture.AgentProvider.Calls.Select(c => c.Request.Prompt).ToList();
            prompts.Should().HaveCount(4, "Phase 0, Phase 1, Phase 2a and the aggregation each run once");
            prompts.Should().OnlyHaveUniqueItems("a repeated prompt means a phase was skipped and the aggregation was retried");

            Fixture.IssueProvider.CreatedIssues.Should().ContainSingle();
            var created = Fixture.IssueProvider.CreatedIssues[0];
            created.Title.Should().Contain("Split the handler");
            created.Labels.Should().Equal(AgentLabels.Generated);

            Fixture.RepositoryProvider.MethodCalls.Should().Contain(nameof(IRepositoryProvider.CloneAsync));
        }
        finally
        {
            // The hub counts the created issue on the API host's badge service, which lives as long as the host
            // and which nothing in the fixture reset. ConsolidationPageTests asserts the absolute count there.
            Fixture.ApiServices.GetRequiredService<ConsolidationBadgeService>().Reset();
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the consolidation work item for <see cref="ScanKey"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. The item is inserted Pending and
    /// routed first: the claim refuses an item whose payload has no pinned route, and only a Pending item
    /// can be routed. Returns the work item GUID.
    /// </summary>
    private async Task<Guid> CreateAndClaimConsolidationWorkItemAsync(string agentId, CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(ScanKey, agentSelector: "e2e");

        // The identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment enriches it with
        // the template's repository and issue provider configs, the pipeline configuration and the agent config.
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = ScanKey,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e",
            InitiatedBy = "e2e-consolidation-test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationTemplateId = TemplateId,
            RefactoringFocus = RefactoringScanFocus.Structure,
            AutoDispatch = false,
            PayloadSchemaVersion = 1
        };

        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var entity = await db.WorkItems.FindAsync([workItemId], ct)
                ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after insert");
            entity.TaskType = WorkItemTaskType.Consolidation;
            entity.IssueProviderConfigId = ConsolidationConstants.ProviderConfigId;
            entity.AgentSelector = "e2e";
            entity.ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId);
            entity.Payload = System.Text.Json.JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);
            await db.SaveChangesAsync(ct);
        }

        // Enrichment resolves the agent profile from the "e2e" selector. Seeded after the insert, which routes its
        // own profile for that selector, so the profile named here is the one the route pins.
        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "profile-e2e-real-consolidation",
            DisplayName = "Real consolidation smoke profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        // Spec 056: the dispatch pins the route on the item; the claim refuses an item dispatched without one.
        var route = await Fixture.ApiServices.GetRequiredService<CodingAgent.Api.Dispatch.DispatchRouter>().RouteAsync(workItemId, ct);
        route.Status.Should().Be(CodingAgent.Api.Dispatch.DispatchRouteStatus.Routed);

        await using (var dispatchDb = Fixture.DbContextFactory.CreateDbContext())
        {
            var dispatched = await dispatchDb.WorkItems.FindAsync([workItemId], ct)
                ?? throw new InvalidOperationException($"Work item {workItemId} not found in DB after routing");
            dispatched.Status = WorkItemStatus.Dispatched;
            dispatched.AssignedAgentId = agentId;
            dispatched.DispatchedAt = DateTimeOffset.UtcNow;
            await dispatchDb.SaveChangesAsync(ct);
        }

        return workItemId;
    }
}
