using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.E2ETests.Fakes;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;
using LibGit2Sharp;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Rework over real git: the real agent worker runs an issue whose open pull request already exists, against
/// <see cref="RealGitRepositoryProvider"/> (production git operations on a local bare repository, pull requests
/// in memory). Where <see cref="InMemoryRepositoryProvider"/> always merges clean and lists no agent pull
/// requests, so the rework path never starts, these tests run the whole path:
/// <list type="bullet">
///   <item>The run finds the open pull request and rebases its branch onto <c>main</c>.</item>
///   <item>On a conflict main wins on the conflicting file and the branch's other files stay.</item>
///   <item>The rework agent is told what was dropped (<c>.agent/rework-context.md</c>).</item>
///   <item>The same pull request is updated and force-pushed; no second pull request opens.</item>
///   <item>Identifiers that were dropped and not re-applied are reported on the pull request.</item>
/// </list>
/// The rebase itself is covered on real repositories by <c>RepositoryGitOperationsMergeFromBaseTests</c>.
///
/// <para>
/// Each test uses its own issue id (7301-7305) and the pull request number 51. The branch names start with
/// <c>feature/auto-{issueId}-</c>, or <c>GetAgentPullRequestsAsync</c> does not find the pull request.
/// The seeded <c>.gitignore</c> holds <c>.agent/</c>; without it the run leaves a modified <c>.gitignore</c> in the
/// working tree before the checkout and the rebase.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class RealGitReworkTests : HeadlessE2ETestBase
{
    private const int PrNumber = 51;

    public RealGitReworkTests(E2EFixture fixture) : base(fixture) { }

    // ── Helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>Clones the bare repository, creates <paramref name="branch"/>, commits <paramref name="files"/> on it and pushes it. Returns the branch tip SHA.</summary>
    private static string SeedPrBranch(
        BrainRepoFixture repo, string branch, IReadOnlyDictionary<string, string> files, string message)
    {
        var clonePath = repo.Clone($"seed-{Guid.NewGuid():N}");
        using var r = new Repository(clonePath);
        Commands.Checkout(r, r.CreateBranch(branch));

        foreach (var (path, content) in files)
        {
            var fullPath = Path.Combine(clonePath, path);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            File.WriteAllText(fullPath, content);
        }

        Commands.Stage(r, "*");
        var signature = new Signature("test", "test@example.com", DateTimeOffset.UtcNow);
        var commit = r.Commit(message, signature, signature);
        r.Network.Push(r.Network.Remotes["origin"], $"refs/heads/{branch}:refs/heads/{branch}");
        return commit.Sha;
    }

    /// <summary>Commits <paramref name="files"/> to <c>main</c> of the bare repository from a fresh clone, so the push is a fast-forward.</summary>
    private static void PushToMain(BrainRepoFixture repo, IReadOnlyDictionary<string, string?> files) =>
        repo.CommitAndPush(repo.Clone($"main-{Guid.NewGuid():N}"), files, "main commit");

    /// <summary>
    /// Writes <c>.agent/rework-context.md</c> content into <see cref="Captured"/>, then behaves like a ready analysis.
    /// It must be the first script: <c>CreateBranchStep</c> writes the file before the analysis call, and the
    /// workspace is deleted when the run ends, so this is the only place the test can read it.
    /// </summary>
    private sealed class CapturingAnalysisScript : AgentScript
    {
        public string? Captured { get; private set; }

        public override void Execute(string workspacePath)
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.ReworkContextFilePath);
            Captured = File.Exists(path) ? File.ReadAllText(path) : null;
            new AnalysisScript { Recommendation = "ready", AnalysisContent = new string('x', 200) }.Execute(workspacePath);
        }
    }

    private static Dictionary<string, string> OneFile(string path, string content) => new() { [path] = content };

    /// <summary>The seed of the bare repository. <c>.gitignore</c> holds <c>.agent/</c>, so the run leaves it unchanged.</summary>
    private static Dictionary<string, string> SeedFiles() => new()
    {
        ["README.md"] = "seed\n",
        [".gitignore"] = ".agent/\n",
        ["src/Shared.cs"] = "// base\n"
    };

    /// <summary>Adds the open, non-draft pull request 51 for <paramref name="branch"/> to <paramref name="provider"/>.</summary>
    private static RealGitPullRequest AddOpenPr(RealGitRepositoryProvider provider, string branch)
    {
        var pr = new RealGitPullRequest
        {
            Number = PrNumber,
            BranchName = branch,
            BaseBranch = "main",
            Title = "t",
            Body = "old body",
            IsDraft = false,
            State = PullRequestState.Open
        };
        provider.PullRequests.Add(pr);
        return pr;
    }

    /// <summary>Adds the issue with <paramref name="label"/>, the template and the routed profile, and lets the quality gates pass.</summary>
    private async Task ArrangeIssueAsync(string issueId, string label)
    {
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            IsAuthorVerified = true,
            Identifier = issueId,
            Title = "Rework the branch",
            Description = "## Requirements\nAdd src/Rework.cs.\n\n## Acceptance Criteria\n- [ ] The file exists",
            Labels = [label]
        });

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "real-git-rework-template",
            Name = "Real Git Rework Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.SeedRoutedProfileAsync(new AgentProfile
        {
            Id = "real-git-rework-profile",
            DisplayName = "Real Git Rework Profile",
            JobTemplate = E2ERouting.Key(["e2e"]),
        });

        Fixture.QualityGateValidator.AlwaysPass();
    }

    /// <summary>
    /// Claims a work item for <paramref name="issueId"/>, runs the real worker on it and waits for <c>Succeeded</c>.
    /// The caller owns <paramref name="harness"/> and keeps it alive while it asserts: the final label swap runs in
    /// the hub's completion handler, which a closed connection would cancel.
    /// </summary>
    private async Task RunWorkerToSuccessAsync(RealAgentWorkerHarness harness, string issueId)
    {
        var agentId = $"real-worker-{Guid.NewGuid():N}";
        var workItemId = await CreateAndClaimWorkItemAsync(issueId, agentId);

        await harness.StartAsync(
            agentHubUrl: Fixture.AgentHubUrl,
            apiKey: Fixture.ApiKey,
            agentId: agentId,
            workItemId: workItemId.ToString(),
            fakeProviders: Fixture.FakeProviders,
            qualityGateValidator: Fixture.QualityGateValidator);

        await harness.WaitForCompletionAsync(timeout: TimeSpan.FromSeconds(30));
        await WaitForWorkItemStatusAsync(workItemId, WorkItemStatus.Succeeded, timeout: TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// Creates a work item for <paramref name="issueId"/> and marks it Dispatched to
    /// <paramref name="agentId"/> in the database, as the dispatch path would.
    /// Returns the claimed work item GUID. Copied from <c>RealGitSmokeTests</c>: each class keeps its own.
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

    /// <summary>
    /// The <c>agent:</c> labels of <paramref name="issueId"/>. The issue provider fake replaces its entries from the
    /// hub thread, and a read that overlaps a write can throw, so the read is repeated.
    /// </summary>
    private async Task<List<string>> AgentLabelsAsync(string issueId)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var issue = await Fixture.IssueProvider.GetIssueAsync(issueId, CancellationToken.None);
                return issue.Labels.Where(l => l.StartsWith("agent:", StringComparison.Ordinal)).ToList();
            }
            catch (InvalidOperationException) when (attempt < 5)
            {
                // Read again: each write is a single step.
            }
        }
    }

    /// <summary>
    /// Waits up to 10 s until the <c>agent:</c> labels of <paramref name="issueId"/> equal <paramref name="expected"/>, and
    /// returns the labels found. The run's own label swap is the last thing the hub does, so it can land after the work
    /// item is Succeeded. The assertion on the returned labels reports what the issue ended with.
    /// </summary>
    private async Task<List<string>> WaitForAgentLabelsAsync(string issueId, params string[] expected)
    {
        try
        {
            await WaitUntilAsync(
                async () => (await AgentLabelsAsync(issueId)).ToHashSet().SetEquals(expected),
                TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // The assertion that follows reports the labels found.
        }

        return await AgentLabelsAsync(issueId);
    }

    /// <summary>
    /// Downcasts to the concrete <see cref="HousekeepingService"/> and sets the seams a single deterministic cycle needs:
    /// synchronous fire-and-forget and no mergeability re-probe delay. Copied from <c>HousekeepingE2ETests</c>.
    /// </summary>
    private HousekeepingService GetHousekeepingService()
    {
        var svc = (HousekeepingService)Fixture.SchedulerFactory.HousekeepingService;
        svc.FireAndForget = task => task;
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;
        return svc;
    }

    private static PullRequestSummary MakePr(int number, string branchName, IReadOnlyList<string> labels) => new()
    {
        Number = number,
        Identifier = number.ToString(),
        Title = $"PR #{number}",
        Description = string.Empty,
        Labels = labels,
        BranchName = branchName,
        TargetBranch = "main",
        Url = $"https://github.com/e2e-org/e2e-repo/pull/{number}",
        IsDraft = false,
    };

    private static string Normalized(string? content) => content?.ReplaceLineEndings("\n") ?? string.Empty;

    // ── G2: clean rebase ────────────────────────────────────────────────────────────

    /// <summary>
    /// The branch's pull request is open and main has moved without touching the branch's files. The run rebases
    /// the branch onto main cleanly, adds its work, force-pushes the same branch and updates pull request 51.
    ///
    /// <para>
    /// A clean rebase leaves the rework prompt empty (no conflicts, no review comments, not a draft), so the pipeline
    /// skips code generation and the third script runs in the pre-PR cleanup call. The file lands in the commit
    /// either way.
    /// </para>
    ///
    /// Asserts:
    /// <list type="bullet">
    ///   <item>The WorkItem ends <c>Succeeded</c>, and the only pull request is 51, ready, with a new body.</item>
    ///   <item>The remote branch has the branch's file, main's file and the new file, with both older commit messages.</item>
    ///   <item>The remote branch tip is not the seeded tip, so the force push landed; <c>main</c> has no new file.</item>
    ///   <item>The issue ends <c>agent:done</c> alone.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task WhenReworkRebasesCleanly_ShouldForcePushAndUpdateTheSamePr()
    {
        const string issueId = "7301";
        const string branch = "feature/auto-7301-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        // Declared before the harness: the harness disposes first, then the fixture resets git's read-only files.
        using var repo = BrainRepoFixture.Create(SeedFiles());
        await using var harness = new RealAgentWorkerHarness();
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await ArrangeIssueAsync(issueId, AgentLabels.Next);

        var oldTip = SeedPrBranch(repo, branch, OneFile("src/Branch.cs", "// branch\n"), "branch commit");
        PushToMain(repo, new Dictionary<string, string?> { ["src/Main.cs"] = "// main\n" });
        AddOpenPr(provider, branch);

        // Analysis, adversarial review, then the file the run adds.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript { Files = OneFile("src/Rework.cs", "// rework\n") });

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerToSuccessAsync(harness, issueId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        var pr = provider.PullRequests.Should().ContainSingle().Subject;
        pr.Number.Should().Be(PrNumber);
        pr.IsDraft.Should().BeFalse();
        pr.Body.Should().NotBe("old body");

        provider.ReadRemoteFile(branch, "src/Branch.cs").Should().NotBeNull("the branch's own file stays");
        provider.ReadRemoteFile(branch, "src/Main.cs").Should().NotBeNull("the branch was rebased onto main");
        provider.ReadRemoteFile(branch, "src/Rework.cs").Should().NotBeNull("the run's work is committed on the branch");

        var messages = provider.RemoteCommitMessages(branch);
        messages.Should().Contain("main commit");
        messages.Should().Contain("branch commit");

        using var bare = new Repository(repo.RemotePath);
        var newTip = bare.Branches[branch]?.Tip.Sha;
        newTip.Should().NotBeNullOrEmpty();
        newTip.Should().NotBe(oldTip, "the force push replaced the seeded branch tip");

        provider.ReadRemoteFile("main", "src/Rework.cs").Should().BeNull("the work goes to the branch, never to main");

        (await WaitForAgentLabelsAsync(issueId, AgentLabels.Done)).Should().BeEquivalentTo([AgentLabels.Done]);
    }

    // ── G3: force-resolved conflict ─────────────────────────────────────────────────

    /// <summary>
    /// The branch and main both changed <c>src/Shared.cs</c>. The rebase force-resolves it with main's version,
    /// keeps the branch's other file, and writes <c>.agent/rework-context.md</c> before the analysis call, so the
    /// rework agent is told what was dropped.
    ///
    /// Asserts (line endings normalized):
    /// <list type="bullet">
    ///   <item>The WorkItem ends <c>Succeeded</c>.</item>
    ///   <item>On the remote branch <c>src/Shared.cs</c> is main's version, <c>src/BranchOnly.cs</c> is the branch's, and the new file is there.</item>
    ///   <item>The context file the analysis script saw has the title, the file heading, the dropped heading and both diffs.</item>
    ///   <item>The only pull request is 51.</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task WhenReworkHitsConflict_MainShouldWinAndAgentShouldGetTheContext()
    {
        const string issueId = "7302";
        const string branch = "feature/auto-7302-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        using var repo = BrainRepoFixture.Create(SeedFiles());
        await using var harness = new RealAgentWorkerHarness();
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await ArrangeIssueAsync(issueId, AgentLabels.Next);

        SeedPrBranch(
            repo,
            branch,
            new Dictionary<string, string>
            {
                ["src/Shared.cs"] = "// branch version\n",
                ["src/BranchOnly.cs"] = "// branch only\n"
            },
            "branch commit");
        PushToMain(repo, new Dictionary<string, string?> { ["src/Shared.cs"] = "// main version\n" });
        AddOpenPr(provider, branch);

        var analysis = new CapturingAnalysisScript();
        Fixture.AgentProvider
            .Enqueue(analysis)
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript { Files = OneFile("src/Rework.cs", "// rework\n") });

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerToSuccessAsync(harness, issueId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        Normalized(provider.ReadRemoteFile(branch, "src/Shared.cs")).Should().Be("// main version\n", "main wins on the conflicting file");
        Normalized(provider.ReadRemoteFile(branch, "src/BranchOnly.cs")).Should().Be("// branch only\n", "the branch's other files stay");
        Normalized(provider.ReadRemoteFile(branch, "src/Rework.cs")).Should().Be("// rework\n");

        analysis.Captured.Should().NotBeNull("the rework context exists when the analysis agent runs");
        var context = Normalized(analysis.Captured);
        context.Should().Contain("# Rework context: changes dropped by the rebase onto main");
        context.Should().Contain("## `src/Shared.cs`");
        context.Should().Contain("### Your branch's change (dropped)");
        context.Should().Contain("+// branch version");
        context.Should().Contain("+// main version");

        provider.PullRequests.Should().ContainSingle().Which.Number.Should().Be(PrNumber);
    }

    // ── G8: dropped identifiers ─────────────────────────────────────────────────────

    /// <summary>
    /// The branch declared <c>DroppedWidget</c> in <c>src/Shared.cs</c>, which main changed too, so the rebase
    /// dropped it. The code generation script writes another file and never restores the type, so the pull request
    /// body lists it under "Dropped changes not re-applied".
    ///
    /// Asserts: the WorkItem ends <c>Succeeded</c>, and the body has the section heading and the
    /// <c>`src/Shared.cs`: `DroppedWidget`</c> line.
    /// </summary>
    [Fact]
    public async Task WhenDroppedIdentifierIsNotReapplied_ShouldReportItOnThePr()
    {
        const string issueId = "7303";
        const string branch = "feature/auto-7303-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        using var repo = BrainRepoFixture.Create(SeedFiles());
        await using var harness = new RealAgentWorkerHarness();
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await ArrangeIssueAsync(issueId, AgentLabels.Next);

        SeedPrBranch(repo, branch, OneFile("src/Shared.cs", "public class DroppedWidget { }\n"), "branch commit");
        PushToMain(repo, new Dictionary<string, string?> { ["src/Shared.cs"] = "// main version\n" });
        AddOpenPr(provider, branch);

        // Code generation does not restore DroppedWidget.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript { Files = OneFile("src/Other.cs", "// other\n") });

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerToSuccessAsync(harness, issueId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        var body = provider.PullRequests.Should().ContainSingle().Subject.Body;
        body.Should().Contain("## ⚠️ Dropped changes not re-applied");
        body.Should().Contain("- `src/Shared.cs`: `DroppedWidget`");
    }

    /// <summary>
    /// Control for <see cref="WhenDroppedIdentifierIsNotReapplied_ShouldReportItOnThePr"/>: the same dropped
    /// <c>DroppedWidget</c>, but code generation writes it back into <c>src/Shared.cs</c>, so the pull request
    /// body has no "Dropped changes not re-applied" section.
    /// </summary>
    [Fact]
    public async Task WhenDroppedIdentifierIsReapplied_ShouldNotReportIt()
    {
        const string issueId = "7304";
        const string branch = "feature/auto-7304-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        using var repo = BrainRepoFixture.Create(SeedFiles());
        await using var harness = new RealAgentWorkerHarness();
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await ArrangeIssueAsync(issueId, AgentLabels.Next);

        SeedPrBranch(repo, branch, OneFile("src/Shared.cs", "public class DroppedWidget { }\n"), "branch commit");
        PushToMain(repo, new Dictionary<string, string?> { ["src/Shared.cs"] = "// main version\n" });
        AddOpenPr(provider, branch);

        // Code generation re-applies DroppedWidget on top of main's version.
        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript
            {
                Files = OneFile("src/Shared.cs", "// main version\npublic class DroppedWidget { }\n")
            });

        // ── Act ────────────────────────────────────────────────────────────────────

        await RunWorkerToSuccessAsync(harness, issueId);

        // ── Assert ─────────────────────────────────────────────────────────────────

        Normalized(provider.ReadRemoteFile(branch, "src/Shared.cs"))
            .Should().Be("// main version\npublic class DroppedWidget { }\n", "the re-applied type is what makes the report empty");
        provider.PullRequests.Should().ContainSingle().Subject.Body.Should().NotContain("Dropped changes not re-applied");
    }

    // ── L3: housekeeping to rework ──────────────────────────────────────────────────

    /// <summary>
    /// Closes the loop from a conflicted pull request to the rework run. The issue is <c>agent:done</c> and its pull
    /// request conflicts with main. One housekeeping cycle relabels the issue <c>agent:next</c>; the real worker then
    /// reworks the same pull request, main wins on the conflicting file, and the issue ends <c>agent:done</c>.
    ///
    /// <para>
    /// The pull request keeps its <c>Conflicted</c> mergeability during the run. That is harmless because no pipeline
    /// provider is configured, so the run polls no external CI.
    /// </para>
    ///
    /// Asserts: the labels are exactly <c>agent:next</c> after housekeeping; then the WorkItem ends <c>Succeeded</c>,
    /// pull request 51 is the only one, <c>src/Shared.cs</c> is main's version, the new file is on the branch and the
    /// labels are exactly <c>agent:done</c>.
    /// </summary>
    [Fact]
    public async Task WhenPrConflicts_HousekeepingRelabelShouldLeadToReworkOfTheSamePr()
    {
        const string issueId = "7305";
        const string branch = "feature/auto-7305-x";

        // ── Arrange ────────────────────────────────────────────────────────────────

        using var repo = BrainRepoFixture.Create(SeedFiles());
        await using var harness = new RealAgentWorkerHarness();
        var provider = new RealGitRepositoryProvider(repo.RemotePath);
        Fixture.FakeProviders.RepositoryProviderOverride = provider;

        await ArrangeIssueAsync(issueId, AgentLabels.Done);

        SeedPrBranch(repo, branch, OneFile("src/Shared.cs", "// branch version\n"), "branch commit");
        PushToMain(repo, new Dictionary<string, string?> { ["src/Shared.cs"] = "// main version\n" });

        var pr = AddOpenPr(provider, branch);
        pr.Labels.Add(AgentLabels.Done);
        provider.PrMergeability[PrNumber] = PrMergeabilityStatus.Conflicted;
        provider.PrLinkedIssues[PrNumber] = new List<string> { issueId };

        // ── Act 1: one housekeeping cycle, before any work item exists ──────────────

        await GetHousekeepingService().ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = provider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = [MakePr(PrNumber, branch, [AgentLabels.Done])],
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            CancellationToken.None);

        // ── Assert 1 ───────────────────────────────────────────────────────────────

        (await WaitForAgentLabelsAsync(issueId, AgentLabels.Next)).Should().BeEquivalentTo([AgentLabels.Next]);

        // ── Act 2: the rework run ──────────────────────────────────────────────────

        Fixture.AgentProvider
            .EnqueueReadyAnalysis()
            .EnqueueReadyAnalysis()
            .Enqueue(new WriteFilesScript { Files = OneFile("src/Rework.cs", "// rework\n") });

        await RunWorkerToSuccessAsync(harness, issueId);

        // ── Assert 2 ───────────────────────────────────────────────────────────────

        provider.PullRequests.Should().ContainSingle().Which.Number.Should().Be(PrNumber);
        Normalized(provider.ReadRemoteFile(branch, "src/Shared.cs")).Should().Be("// main version\n", "main wins on the conflicting file");
        provider.ReadRemoteFile(branch, "src/Rework.cs").Should().NotBeNull("the rework run's work is committed on the branch");

        (await WaitForAgentLabelsAsync(issueId, AgentLabels.Done)).Should().BeEquivalentTo([AgentLabels.Done]);
    }
}
