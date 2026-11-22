using AwesomeAssertions;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Real agent worker runs against <see cref="RealGitRepositoryProvider"/> whose outcome depends on real git or on the
/// state of the pull request: an agent that changes nothing, a pull request that is already merged or closed when the
/// run starts, a blacklisted path, and a branch that changes quality-gate configuration.
/// <see cref="InMemoryRepositoryProvider"/> cannot reach the first path, because its <c>HasCommitsAheadAsync</c>
/// always returns true, and the blacklist runs only inside the git operations.
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealGitOutcomeTests : HeadlessE2ETestBase
{
    public RealGitOutcomeTests(E2EFixture fixture) : base(fixture) { }

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
    /// <paramref name="linkedPullRequest"/> is written to the payload as it is. Enrichment keeps it, and the worker
    /// copies it to the run, so the run starts as if the pull request had been found at dispatch.
    /// </para>
    /// </summary>
    private async Task<Guid> CreateAndClaimWorkItemAsync(
        string issueId,
        string agentId,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        LinkedPullRequest? linkedPullRequest = null,
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
            LinkedPullRequest = linkedPullRequest,
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

    /// <summary>
    /// Seeds the issue with <c>agent:next</c>, the template that owns <c>repo-e2e</c>, and the routed profile, and lets
    /// every quality gate pass. <paramref name="blacklistedPaths"/> is set on the template when given.
    /// </summary>
    private async Task SeedRunAsync(string issueId, string title, IReadOnlyList<string>? blacklistedPaths = null)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = title,
            Description = "## Requirements\nDo the work.\n\n## Acceptance Criteria\n- [ ] The work is done",
            Labels = ["agent:next"]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-git-outcome-template",
            Name = "Real Git Outcome Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            BlacklistedPaths = blacklistedPaths
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-git-outcome-profile",
            DisplayName = "Real Git Outcome Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        Fixture.QualityGateValidator.AlwaysPass();
    }

    /// <summary>
    /// Waits until the labels of <paramref name="issueId"/> that start with <c>agent:</c> are exactly
    /// <paramref name="expected"/>. The terminal label is set through the API after the work item has its terminal
    /// status, so it can land later. The final assertion names the labels that were found.
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

    // ── G1: the agent changes nothing ───────────────────────────────────────────────

    /// <summary>
    /// The code generation script writes nothing. The seeded <c>.gitignore</c> already holds <c>.agent/</c>, so
    /// <c>EnsureAgentGitignoreStep</c> edits nothing either, and the branch has no commit ahead of <c>main</c>.
    /// The run ends <c>Failed</c> with <c>AgentError</c>, opens no pull request and sets <c>agent:error</c>.
    ///
    /// The branch itself is pushed before the pipeline finds no commits ahead, so the test does not look at the remote.
    /// </summary>
    [Fact]
    public async Task WhenAgentChangesNothing_ShouldFailWithAgentErrorAndOpenNoPr()
    {
        const string issueId = "7201";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = "seed\n",
            [".gitignore"] = ".agent/\n"
        });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await SeedRunAsync(issueId, "Change nothing");

        // Analysis, adversarial review, code generation (writes nothing).
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .EnqueueCodeGen();

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

        var item = await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Failed, timeout: TimeSpan.FromSeconds(10));
        item.ErrorMessage.Should().Be("Agent did not produce any changes. No commits ahead of base branch.");
        item.FailureReason.Should().Be(FailureReason.AgentError);

        provider.PullRequests.Should().BeEmpty();
        await AssertAgentLabelsAsync(issueId, AgentLabels.Error);
    }

    // ── G5: the linked pull request is already merged or closed ─────────────────────

    /// <summary>
    /// The work item's payload links pull request 41, which is already merged. <c>CreateBranchStep</c> ends the run
    /// before any git change and before any agent call: the work item is <c>Succeeded</c>, the issue is
    /// <c>agent:done</c>, the pull request is untouched and no branch is pushed.
    /// </summary>
    [Fact]
    public async Task WhenLinkedPrAlreadyMerged_ShouldSucceedWithoutAgentCallsOrCommits()
    {
        const string issueId = "7202";
        const string branchName = "feature/auto-7202-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = "seed\n",
            [".gitignore"] = ".agent/\n"
        });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await SeedRunAsync(issueId, "Already merged");

        provider.PullRequests.Add(new RealGitPullRequest
        {
            Number = 41,
            BranchName = branchName,
            BaseBranch = "main",
            Title = "t",
            Body = "b",
            IsDraft = false,
            State = PullRequestState.Merged
        });

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(
            issueId,
            agentId,
            linkedPullRequest: new LinkedPullRequest
            {
                Number = 41,
                BranchName = branchName,
                IsDraft = false,
                Url = "https://github.com/e2e-org/e2e-repo/pull/41"
            });

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
        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);

        provider.PullRequests.Should().ContainSingle().Which.Number.Should().Be(41);
        provider.ReadRemoteFile(branchName, "README.md").Should().BeNull("no branch was pushed");
        // A run that got past the merged guard would push a branch named from the issue title and run id, not the
        // invented name above, so the check on that name alone cannot catch it. Check the remote's real branch list.
        (await provider.ListAgentBranchesAsync(CancellationToken.None)).Should()
            .BeEmpty("no agent branch was pushed to the remote");
        Fixture.AgentProvider.Calls.Should().BeEmpty("the run ends before any agent call");
    }

    /// <summary>
    /// The work item's payload links pull request 42, which was closed without a merge. The run ends before any agent
    /// call: the work item is <c>Cancelled</c> and the issue is <c>agent:cancelled</c>.
    /// </summary>
    [Fact]
    public async Task WhenLinkedPrAlreadyClosed_ShouldEndCancelledWithCancelledLabel()
    {
        const string issueId = "7203";
        const string branchName = "feature/auto-7203-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = "seed\n",
            [".gitignore"] = ".agent/\n"
        });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await SeedRunAsync(issueId, "Already closed");

        provider.PullRequests.Add(new RealGitPullRequest
        {
            Number = 42,
            BranchName = branchName,
            BaseBranch = "main",
            Title = "t",
            Body = "b",
            IsDraft = false,
            State = PullRequestState.Closed
        });

        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(
            issueId,
            agentId,
            linkedPullRequest: new LinkedPullRequest
            {
                Number = 42,
                BranchName = branchName,
                IsDraft = false,
                Url = "https://github.com/e2e-org/e2e-repo/pull/42"
            });

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

        // The run history shows Cancelled, not PrClosed, so the test asserts the status and the label only.
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Cancelled, timeout: TimeSpan.FromSeconds(10));
        await AssertAgentLabelsAsync(issueId, AgentLabels.Cancelled);

        Fixture.AgentProvider.Calls.Should().BeEmpty("the run ends before any agent call");
    }

    // ── G6: a blacklisted path stays out of the remote ──────────────────────────────

    /// <summary>
    /// The template blacklists <c>secrets</c>. The code generation script writes <c>src/Allowed.cs</c> and
    /// <c>secrets/key.txt</c>. The commit unstages the blacklisted file, so the pushed branch carries the allowed file
    /// only, and the run still ends <c>Succeeded</c> with a pull request.
    /// </summary>
    [Fact]
    public async Task WhenAgentWritesBlacklistedPath_ShouldKeepItOutOfTheRemoteBranch()
    {
        const string issueId = "7204";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = "seed\n",
            [".gitignore"] = ".agent/\n"
        });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await SeedRunAsync(issueId, "Write a blacklisted path", blacklistedPaths: ["secrets"]);

        // Analysis, adversarial review, code generation (writes an allowed and a blacklisted file).
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript
            {
                Files = new Dictionary<string, string>
                {
                    ["src/Allowed.cs"] = "// ok\n",
                    ["secrets/key.txt"] = "do-not-commit\n"
                }
            });

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

        var allowedFile = provider.ReadRemoteFile(pr.BranchName, "src/Allowed.cs");
        allowedFile.Should().NotBeNull("the file outside the blacklist must be committed and pushed");
        allowedFile!.ReplaceLineEndings("\n").Should().Be("// ok\n");
        provider.ReadRemoteFile(pr.BranchName, "secrets/key.txt").Should().BeNull("the blacklisted file must not reach the remote");
    }

    // ── G7: the branch changes quality-gate configuration ───────────────────────────

    /// <summary>
    /// The code generation script writes <c>sonar-project.properties</c>, a quality-gate configuration file, and a
    /// source file. The pipeline leaves the pull request a draft and adds a warning to its body. The run itself still
    /// ends <c>Succeeded</c> with <c>agent:done</c>, because the run was not a draft run.
    /// </summary>
    [Fact]
    public async Task WhenBranchChangesGateConfig_ShouldLeavePrDraftWithWarning()
    {
        const string issueId = "7205";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(new Dictionary<string, string>
        {
            ["README.md"] = "seed\n",
            [".gitignore"] = ".agent/\n"
        });
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await SeedRunAsync(issueId, "Change the gate configuration");

        // Analysis, adversarial review, code generation (writes a gate configuration file and a source file).
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript
            {
                Files = new Dictionary<string, string>
                {
                    ["sonar-project.properties"] = "sonar.exclusions=**\n",
                    ["src/Feature.cs"] = "// f\n"
                }
            });

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
        pr.IsDraft.Should().BeTrue("a branch that changes quality-gate configuration stays a draft");
        pr.Body.Should().Contain("## ⚠️ Quality-gate configuration changed");
        pr.Body.Should().Contain("`sonar-project.properties`");

        await AssertAgentLabelsAsync(issueId, AgentLabels.Done);
    }
}
