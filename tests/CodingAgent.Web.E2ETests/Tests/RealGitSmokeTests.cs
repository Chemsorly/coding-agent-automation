using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Smoke test that runs the real agent worker against <see cref="RealGitRepositoryProvider"/>: the production
/// git operations run against a local bare repository, and the pull requests are kept in memory.
/// Unlike <see cref="InMemoryRepositoryProvider"/>, which runs no git, a code generation run that writes nothing
/// gives no useful commit here, so the branch and pull request the test asserts on really come from the run.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealGitSmokeTests : HeadlessE2ETestBase
{
    public RealGitSmokeTests(E2EFixture fixture) : base(fixture) { }

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

    // ── Implementation run on a real bare repository ──────────────────────────────────

    /// <summary>
    /// A real agent worker runs an <c>agent:next</c> issue to the end with <see cref="RealGitRepositoryProvider"/>
    /// selected. The code generation script writes <c>src/Added.cs</c>, so the run commits it, pushes the
    /// <c>feature/auto-5101-*</c> branch to the bare repository and opens one ready pull request.
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item>The WorkItem ends <c>Succeeded</c>.</item>
    ///   <item>The provider holds one open, non-draft pull request from the issue's branch onto <c>main</c>.</item>
    ///   <item>The branch on the bare repository carries the generated file, but neither <c>.agent/analysis.md</c> nor <c>main</c> does.</item>
    ///   <item>The issue ends <c>agent:done</c>, and the agent's pull request is found, then closed, through the provider.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task WhenImplementationRunFinishes_ShouldPushBranchAndOpenPullRequestOnBareRepo()
    {
        const string issueId = "5101";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string> { ["README.md"] = "seed\n" });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add a source file",
            Description = "## Requirements\nAdd src/Added.cs.\n\n## Acceptance Criteria\n- [ ] The file exists",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-git-template",
            Name = "Real Git Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-git-profile",
            DisplayName = "Real Git Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        // Analysis, adversarial review, code generation (writes the file), pre-PR cleanup.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript { Files = new Dictionary<string, string> { ["src/Added.cs"] = "// added\n" } })
            .EnqueueCodeGen();

        Fixture.QualityGateValidator.AlwaysPass();

        var agentId = $"real-worker-{Guid.NewGuid():N}";
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

        var pr = provider.PullRequests.Should().ContainSingle().Subject;
        pr.BranchName.Should().StartWith("feature/auto-5101-");
        pr.BaseBranch.Should().Be("main");
        pr.IsDraft.Should().BeFalse();
        pr.State.Should().Be(PullRequestState.Open);

        var addedFile = provider.ReadRemoteFile(pr.BranchName, "src/Added.cs");
        addedFile.Should().NotBeNull("the code generation script's file must be committed and pushed");
        addedFile!.ReplaceLineEndings("\n").Should().Be("// added\n");
        provider.ReadRemoteFile(pr.BranchName, ".agent/analysis.md").Should().BeNull();
        provider.ReadRemoteFile("main", "src/Added.cs").Should().BeNull();

        var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
        issue.Labels.Should().Contain(AgentLabels.Done);

        var agentPrs = await provider.GetAgentPullRequestsAsync(issueId, CancellationToken.None);
        agentPrs.Should().ContainSingle().Which.Number.Should().Be(pr.Number);

        await provider.ClosePullRequestAsync(pr.Number, CancellationToken.None);
        (await provider.GetAgentPullRequestsAsync(issueId, CancellationToken.None)).Should().BeEmpty();
    }
}
