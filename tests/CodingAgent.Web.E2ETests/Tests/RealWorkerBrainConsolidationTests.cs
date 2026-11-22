using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Brain;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Runs a brain consolidation through the real <see cref="CodingAgent.Agent.WorkItemAgentService"/> in-process, with the
/// real <see cref="CodingAgent.Infrastructure.Git.BrainUpdateService"/> and a <see cref="RealGitRepositoryProvider"/>
/// over a bare brain repository, so the <see cref="CodingAgent.Agent.Executors.BrainConsolidationExecutor"/> commits and
/// pushes to a real <c>main</c>. <see cref="ConsolidationPageTests"/> reports a canned result and never sees what reaches
/// the brain; these tests assert on the bare repository.
///
/// <para>
/// The workspace of a brain consolidation is the brain clone itself, not a <c>.brain/</c> subfolder, so the review
/// script edits <c>general/SKILL.md</c> directly under the workspace path. The seed is fully v2-shaped
/// (<c>README.md</c> equals <see cref="BrainReadmeTemplate.Text"/>, marked skill and inbox files): the executor would
/// otherwise archive or rewrite files and add commits that the assertions do not name. The adversarial review is off,
/// and the diff-summary call that follows the review script is unscripted: it returns exit 0 without a diff file, which
/// skips the review.
/// </para>
///
/// <para>
/// Issue #4102 also expects the history outcome to carry the push details, but a review run's outcome does not: the
/// executor returns <c>reviewOutcome ?? new BrainConsolidationOutcome { PushAttempts, BaseBranch, PushedCommitSha, … }</c>,
/// and a parsed review summary always yields <c>reviewOutcome</c>. Measured against the product, with the assertions
/// written as the issue states them:
/// <list type="bullet">
///   <item>scenario 1: <c>PushAttempts</c> expected 1, observed 0; <c>BaseBranch</c> expected "main", observed null;
///         <c>PushedCommitSha</c> expected the tip of <c>main</c> in the bare repository, observed null</item>
///   <item>scenario 2: <c>PushAttempts</c> expected 2, observed 0</item>
/// </list>
/// Those assertions are left out, not changed to the observed values. The tests assert what reached the brain
/// repository and, for the history outcome, only the review count (<c>Appended</c>). The merge of scenario 2 still shows:
/// a non-fast-forward push that was not merged would end the run Failed with "Push failed".
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealWorkerBrainConsolidationTests : HeadlessE2ETestBase
{
    private const string BrainConfigId = "brain-e2e";
    private const string SeedSkill = "<!-- brain:v2 skill -->\n# General\n## Lessons\n- [2+] For retries, use exponential backoff (#1).\n";
    private const string OldInbox = "inbox/2026-10-01_old.md";
    private const string NewLine = "- [2+] For flaky gates, rerun them once (#2).";

    public RealWorkerBrainConsolidationTests(E2EFixture fixture) : base(fixture) { }

    // Edits the brain as the review agent does, and writes the summary.
    private sealed class BrainReviewScript(Action? duringCall = null) : AgentScript
    {
        public override void Execute(string workspacePath)
        {
            duringCall?.Invoke();
            File.AppendAllText(Path.Combine(workspacePath, "general", "SKILL.md"), NewLine + "\n");
            File.Delete(Path.Combine(workspacePath, "inbox", "2026-10-01_old.md"));
            var summary = Path.Combine(workspacePath, AgentWorkspacePaths.BrainReviewSummaryFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(summary)!);
            File.WriteAllText(summary, """{"appended":[{"scope":"general","file":"general/SKILL.md","text":"For flaky gates, rerun them once"}]}""");
        }
    }

    private static readonly string ScopeKey =
        ConsolidationService.ScopeKey(ConsolidationRunType.BrainConsolidation, BrainConfigId, "brain-template");

    // ── 1: the review's changes reach the brain ─────────────────────────────────────

    [Fact]
    public async Task WhenReviewEditsSkillAndClearsInbox_ShouldApplyThemToTheBareRepo()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        using var brain = CreateBrain();
        var provider = new RealGitRepositoryProvider(brain.RemotePath);
        Fixture.FakeProviders.RepositoryProvidersByConfigId[BrainConfigId] = provider;

        await SeedAsync();
        Fixture.AgentProvider.Enqueue(new BrainReviewScript());

        var agentId = $"real-worker-brain-consolidation-{Guid.NewGuid():N}";
        var workItemId = await ClaimAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var files = brain.RemoteFiles();
        files["general/SKILL.md"].Should().Contain(NewLine).And.Contain("For retries, use exponential backoff");
        files.Should().NotContainKey(OldInbox);

        // The review commit message also shows that validation did not refuse the review.
        provider.RemoteCommitMessages("main")[0].Should().Be(BrainLayout.ReviewCommitMessage(workItemId.ToString()));

        // TODO [WARNING] Scenarios 1 and 2 omit the history assertions the issue requires (PushAttempts is 1 / 2,
        // BaseBranch is "main", PushedCommitSha is the tip of main in the bare repo); only Appended is asserted. A review
        // run's outcome carries no push details (BrainConsolidationExecutor returns reviewOutcome ?? new
        // BrainConsolidationOutcome { PushAttempts, ... }), so the issue's rule applies: the PR description names the
        // expected and observed values. Restore the assertions if the product exposes push details for review runs.
        var run = await WaitForHistoryAsync(r => r.IssueIdentifier == ScopeKey);
        run.BrainConsolidationOutcome.Should().NotBeNull();
        run.BrainConsolidationOutcome!.Appended.Should().Be(1);
    }

    // ── 2: another run pushed meanwhile ─────────────────────────────────────────────

    [Fact]
    public async Task WhenAnotherRunPushesDuringReview_ShouldMergeAndLandBoth()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        using var brain = CreateBrain();
        var provider = new RealGitRepositoryProvider(brain.RemotePath);
        Fixture.FakeProviders.RepositoryProvidersByConfigId[BrainConfigId] = provider;

        await SeedAsync();

        // The other run pushes after the consolidation cloned the brain, so the consolidation's push is not a fast-forward.
        Fixture.AgentProvider.Enqueue(new BrainReviewScript(() => brain.CommitAndPush(
            brain.Clone("other-run"),
            new Dictionary<string, string?> { ["inbox/2026-10-02_other.md"] = $"{BrainLayout.InboxMarker}\nother lesson\n" },
            "other run")));

        var agentId = $"real-worker-brain-consolidation-{Guid.NewGuid():N}";
        var workItemId = await ClaimAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        // An unmerged non-fast-forward push would end the run Failed with "Push failed".
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, TimeSpan.FromSeconds(10));

        var files = brain.RemoteFiles();
        files.Should().ContainKey("inbox/2026-10-02_other.md");
        files["general/SKILL.md"].Should().Contain(NewLine);
        files.Should().NotContainKey(OldInbox);

        var messages = provider.RemoteCommitMessages("main");
        messages.Should().Contain("other run");

        // The merge resets to the remote and recommits the review with the review commit message on top of the other run.
        messages[0].Should().Be(BrainLayout.ReviewCommitMessage(workItemId.ToString()));

        // TODO [WARNING] Scenario 2 omits PushAttempts == 2, so nothing here names the non-fast-forward retry: a
        // regression that drops the retry but still lands both sets of changes would not be caught by name. A review run's
        // outcome carries no push attempts (observed 0, expected 2); restore the assertion if the product exposes them.
    }

    // ── 3: a read-only brain is refused ─────────────────────────────────────────────

    [Fact]
    public async Task WhenBrainIsReadOnly_ShouldRefuseBeforeAnyAgentCall()
    {
        // ── Arrange ────────────────────────────────────────────────────────────────

        using var brain = CreateBrain();
        var provider = new RealGitRepositoryProvider(brain.RemotePath);
        Fixture.FakeProviders.RepositoryProvidersByConfigId[BrainConfigId] = provider;

        await SeedAsync(brainReadOnly: true);

        var agentId = $"real-worker-brain-consolidation-{Guid.NewGuid():N}";
        var workItemId = await ClaimAsync(agentId);

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerAsync(agentId, workItemId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        var workItem = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, TimeSpan.FromSeconds(10));

        workItem.ErrorMessage.Should().Contain("The brain is read-only for this template");
        Fixture.AgentProvider.Calls.Should().BeEmpty("a read-only brain is refused before the agent runs");
        provider.RemoteCommitMessages("main").Should().HaveCount(1, "only the seed commit: nothing was pushed");
        brain.RemoteFiles()["general/SKILL.md"].Replace("\r\n", "\n").Should().Be(SeedSkill);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>A bare brain repository in the v2 layout, with one skill file and one inbox file to review.</summary>
    private static BrainRepoFixture CreateBrain() => BrainRepoFixture.Create(new Dictionary<string, string>
    {
        ["README.md"] = BrainReadmeTemplate.Text,
        ["general/SKILL.md"] = SeedSkill,
        [OldInbox] = $"{BrainLayout.InboxMarker}\nold lesson\n"
    });

    /// <summary>
    /// Saves the brain's repository config (its role must be Brain: the worker looks it up by role) and a template that
    /// uses it. The adversarial review stays off, so the queue needs no scripts for it.
    /// </summary>
    private async Task SeedAsync(bool brainReadOnly = false, CancellationToken ct = default)
    {
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = BrainConfigId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "E2E Brain",
            RepositoryRole = RepositoryRole.Brain
        }, ct);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "brain-template",
            Name = "Brain Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            BrainProviderId = BrainConfigId,
            BrainReadOnly = brainReadOnly,
            Enabled = true
        }, ct);

        await Fixture.ConfigStore.UpdatePipelineConfigAsync(
            c => c with { BrainConsolidationReviewEnabled = false }, ct);
    }

    /// <summary>
    /// Creates the brain consolidation work item for <see cref="ScopeKey"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would. The item is inserted Pending and routed
    /// first: the claim refuses an item whose payload has no pinned route, and only a Pending item can be routed.
    /// Returns the work item GUID.
    /// </summary>
    private async Task<Guid> ClaimAsync(string agentId, CancellationToken ct = default)
    {
        var workItemId = await InsertPendingWorkItemAsync(ScopeKey, agentSelector: "e2e");

        // The identity-only payload the dispatcher writes (PayloadSchemaVersion 1); GetAssignment enriches it with the
        // template's repository and brain provider configs, the pipeline configuration and the agent config.
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = ScopeKey,
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
            RepoProviderConfigId = "repo-e2e",
            BrainProviderConfigId = BrainConfigId,
            InitiatedBy = "e2e-brain-consolidation-test",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "e2e",
            TimeoutSeconds = 3600,
            RunType = PipelineRunType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.BrainConsolidation,
            ConsolidationTemplateId = "brain-template",
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
            Id = "profile-e2e-real-brain-consolidation",
            DisplayName = "Real brain consolidation profile",
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

        // The API creates the in-memory run together with the work item (WorkItemStore.CreateAsync). A row inserted
        // straight into the database has none, and without it the hub neither keeps the brain outcome nor writes the
        // run to history. The run is added before the worker starts, so registration picks it up as it does in production.
        Fixture.RunService.AddRun(PipelineRunFactory.CreateFromWorkItem(workItemId, payload)!);

        return workItemId;
    }

    /// <summary>
    /// Starts the real worker for <paramref name="workItemId"/> with the real brain update service, so the review is
    /// validated, committed and pushed against the bare repository, and waits for it to finish.
    /// </summary>
    private async Task RunWorkerAsync(string agentId, Guid workItemId)
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
}
