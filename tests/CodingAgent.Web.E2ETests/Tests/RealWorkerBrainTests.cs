using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// The brain on the real <see cref="CodingAgent.Agent.WorkItemAgentService"/>, in-process against the E2E API host, with
/// the real <see cref="CodingAgent.Infrastructure.Git.BrainUpdateService"/> and a <see cref="RealGitRepositoryProvider"/>
/// over a bare brain repository. The work repository stays on <see cref="InMemoryRepositoryProvider"/>.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerBrainTests : HeadlessE2ETestBase
{
    private const string BrainConfigId = "brain-e2e";
    private const string SeedSkill = "<!-- brain:v2 skill -->\n# General\n## Lessons\n- [2+] For retries, use exponential backoff (#1).\n";

    public RealWorkerBrainTests(E2EFixture fixture) : base(fixture) { }

    // Runs an action during the agent call, then behaves like a ready analysis.
    private sealed class AnalysisWithActionScript(Action<string> action) : AgentScript
    {
        public override void Execute(string workspacePath)
        {
            action(workspacePath);
            new AnalysisScript { Recommendation = "ready", AnalysisContent = new string('x', 200) }.Execute(workspacePath);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    private async Task SeedAsync(string issueId, bool brainReadOnly = false)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Add input validation",
            Description = "## Requirements\nAdd null checks.\n\n## Acceptance Criteria\n- [ ] All methods validate inputs",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = BrainConfigId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "E2E Brain",
            RepositoryRole = RepositoryRole.Brain
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-brain-template",
            Name = "Real Brain Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = BrainConfigId,
            BrainReadOnly = brainReadOnly,
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-brain-profile",
            DisplayName = "Real Brain Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        Fixture.QualityGateValidator.AlwaysPass();
    }

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
    /// The payload carries <c>BrainProviderConfigId</c>: for an implementation run the API copies the brain id from the
    /// payload, and ignores the template's <c>BrainProviderId</c>.
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
            BrainProviderConfigId = BrainConfigId,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
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

    /// <summary>Runs the real worker for <paramref name="workItemId"/> with the real brain update service.</summary>
    private async Task RunAsync(string agentId, Guid workItemId)
    {
        await using var harness = new RealAgentWorkerHarness();
        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator,
            brainUpdateService: new CodingAgent.Infrastructure.Git.BrainUpdateService(Serilog.Log.Logger));

        await harness.WaitForCompletionAsync(TimeSpan.FromSeconds(30));
    }

    private static JobCompletionPayload ReadPayload(string? result)
    {
        result.Should().NotBeNullOrEmpty("only the real POST /status writes Result");
        return JsonSerializer.Deserialize<JobCompletionPayload>(result!, PipelineJsonOptions.Default)!;
    }

    // ── Read-only brain ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WhenBrainIsReadOnly_ShouldInjectSkillsButPushNothing()
    {
        const string issueId = "7902";

        using var brain = BrainRepoFixture.Create(new Dictionary<string, string> { ["README.md"] = "brain\n", ["general/SKILL.md"] = SeedSkill });
        var brainProvider = new RealGitRepositoryProvider(brain.RemotePath);
        Fixture.FakeProviders.RepositoryProvidersByConfigId[BrainConfigId] = brainProvider;

        await SeedAsync(issueId, brainReadOnly: true);

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await RunAsync(agentId, workItemId);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        var payload = ReadPayload(item.Result);
        payload.BrainSkillsResult.Should().Be(BrainSkillsResults.Injected);
        payload.BrainInboxFile.Should().BeNull("only an attempted inbox write sets it");
        payload.BrainPushFailed.Should().NotBe(true);
        brain.RemoteFiles().Keys.Should().NotContain(k => k.StartsWith("inbox/", StringComparison.Ordinal));
        brainProvider.RemoteCommitMessages("main").Should().HaveCount(1);

        // The run reached the post-PR sequence as a ready PR: the description call only happens for a non-draft PR, and the
        // feedback call comes after the slot where reflection would run. Skills were injected, so the brain provider and
        // sync service exist. Only BrainReadOnly can then explain the missing reflection call.
        Fixture.AgentProvider.Calls.Should().Contain(c => c.Request.Prompt.Contains("## Generate a Pull Request Description"));
        Fixture.AgentProvider.Calls.Should().Contain(c => c.Request.Prompt.Contains("# Pipeline Success Feedback"));
        Fixture.AgentProvider.Calls.Should().NotContain(
            c => c.Request.Prompt.Contains(AgentWorkspacePaths.BrainCandidatesFilePath),
            "a read-only brain skips the reflection call");
    }

    // ── A brain write that cannot land ──────────────────────────────────────────────

    /// <summary>
    /// A failed brain write is non-fatal: the run succeeds and the completion payload reports the failure.
    ///
    /// <para>
    /// The write fails here because <see cref="RealGitRepositoryProvider"/> has no <c>WriteFileToBaseBranchAsync</c>: since
    /// #4079 the inbox is written through the provider's API, not pushed with git. So <c>BrainPushMaxRetries = 1</c> and
    /// the push from another run's clone do not cause the failure today. The test guards the non-fatal outcome and its
    /// report, not a conflict.
    /// </para>
    /// </summary>
    [Fact]
    public async Task WhenBrainPushCannotLand_RunShouldStillSucceedAndReportTheFailure()
    {
        const string issueId = "7904";

        using var brain = BrainRepoFixture.Create(new Dictionary<string, string> { ["README.md"] = "brain\n", ["general/SKILL.md"] = SeedSkill });
        var brainProvider = new RealGitRepositoryProvider(brain.RemotePath);
        Fixture.FakeProviders.RepositoryProvidersByConfigId[BrainConfigId] = brainProvider;

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(c => c with { BrainPushMaxRetries = 1 }, CancellationToken.None);
        await SeedAsync(issueId);

        Fixture.AgentProvider
            .Enqueue(new AnalysisWithActionScript(_ => brain.CommitAndPush(brain.Clone("other-run-2"), new Dictionary<string, string?> { ["inbox/2026-01-01_other.md"] = "other run\n" }, "other run")))
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await RunAsync(agentId, workItemId);

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(15));
        var payload = ReadPayload(item.Result);
        payload.BrainPushFailed.Should().BeTrue();
        payload.BrainUpdatesPushed.Should().BeFalse();
        brain.RemoteFiles().Keys.Should().NotContain(k => k.EndsWith($"_{workItemId}.md", StringComparison.Ordinal));
    }
}
