using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Web.TestUtilities;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests for the housekeeping pipeline (conflict rework, branch updates, stale
/// branch cleanup) and orphaned-label recovery. All tests trigger a single deterministic cycle
/// rather than relying on background timers.
///
/// <para>
/// <b>PR number ranges per scenario (singleton state isolation):</b>
/// <c>HousekeepingService</c> is a DI singleton whose internal state (<c>_inFlight</c>,
/// <c>_lastTriggeredAt</c>, <c>_recordedPrOutcomes</c>) is NOT reset by
/// <see cref="E2EFixture.ResetAllAsync"/>. Each scenario uses a distinct PR number range to
/// prevent cross-test cooldown or in-flight state leakage:
/// <list type="bullet">
///   <item>Scenario 1 — PRs 101–109</item>
///   <item>Scenario 2 — PRs 201–209</item>
///   <item>Scenario 3 — PRs 301–309 (branch protection), issues 310–319</item>
///   <item>Scenario 4 — issues 401–409 (no housekeeping PRs)</item>
///   <item>Scenario 5 — PRs 501–509</item>
/// </list>
/// </para>
///
/// <para>
/// <b><c>FireAndForget</c> seam:</b>
/// <c>HousekeepingService.UpdatePullRequestBranchAsync</c> is dispatched via a fire-and-forget
/// wrapper that discards the task in production. Any scenario that asserts on
/// <see cref="CodingAgent.Web.E2ETests.Fakes.InMemoryRepositoryProvider.BranchUpdateCalls"/>
/// must override <c>svc.FireAndForget = task =&gt; task</c> on the concrete service instance to
/// make updates synchronous.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class HousekeepingE2ETests : HeadlessE2ETestBase
{
    public HousekeepingE2ETests(E2EFixture fixture) : base(fixture) { }

    // ── Shared helpers ────────────────────────────────────────────────────

    /// <summary>
    /// Downcasts to the concrete <see cref="HousekeepingService"/> and configures the test
    /// seams that every housekeeping scenario needs: synchronous fire-and-forget (required for
    /// <see cref="CodingAgent.Web.E2ETests.Fakes.InMemoryRepositoryProvider.BranchUpdateCalls"/>
    /// assertions) and zero mergeability re-probe delay (avoids 5-second pauses).
    /// </summary>
    private HousekeepingService GetHousekeepingService()
    {
        var svc = (HousekeepingService)Fixture.SchedulerFactory.HousekeepingService;
        svc.FireAndForget = task => task;           // make UpdatePullRequestBranchAsync synchronous
        svc.MergeabilityReprobeDelay = TimeSpan.Zero; // skip 5-second re-probe pause
        return svc;
    }

    private static PullRequestSummary MakePr(
        int number,
        string branchName,
        bool isDraft = false,
        bool hasAutoMerge = false,
        IReadOnlyList<string>? labels = null) => new()
    {
        Number = number,
        Identifier = number.ToString(),
        Title = $"PR #{number}",
        Description = string.Empty,
        Labels = labels ?? Array.Empty<string>(),
        BranchName = branchName,
        TargetBranch = "main",
        Url = $"https://github.com/e2e-org/e2e-repo/pull/{number}",
        IsDraft = isDraft,
        HasAutoMerge = hasAutoMerge,
    };

    private static IssueDetail MakeIssue(string id, params string[] labels) => new()
    {
        Identifier = id,
        Title = $"Issue #{id}",
        Description = string.Empty,
        Labels = labels,
    };

    // ── Scenario 1 — Conflict rework ──────────────────────────────────────

    /// <summary>
    /// Scenario 1a: a conflicted <c>agent:done</c> PR linked to issue 101 causes the issue to
    /// be relabelled <c>agent:next</c> (conflict rework swap).
    ///
    /// <para>
    /// Chain: <c>HousekeepingService.ExecuteAsync</c> → <c>IssueReworkService.TriggerConflictReworkAsync</c>
    /// → <c>ExtractLinkedIssuesAsync</c> → <c>IIssueProvider.GetIssueAsync</c>
    /// → <c>AgentLabelOperations.SwapAsync</c> (remove <c>agent:done</c>, add <c>agent:next</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario1a_ConflictedPr_RelabelsLinkedIssueToAgentNext()
    {
        // Arrange
        var ct = CancellationToken.None;
        Fixture.IssueProvider.Issues.Add(MakeIssue("101", AgentLabels.Done));
        Fixture.RepositoryProvider.PrMergeability[101] = PrMergeabilityStatus.Conflicted;
        Fixture.RepositoryProvider.PrLinkedIssues[101] = new List<string> { "101" };

        var pr = MakePr(101, "feature/auto-101-fix", labels: new[] { AgentLabels.Done });
        var agentDonePrs = new[] { pr };

        var svc = GetHousekeepingService();

        // Act
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: agent:next was added to issue 101
        var adds = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "101" && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.Contains(AgentLabels.Next, adds);

        // Assert: agent:done was removed from issue 101
        var removes = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "101" && !lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.Contains(AgentLabels.Done, removes);
    }

    /// <summary>
    /// Scenario 1b: when the linked issue has <c>agent:wont-do</c>, the conflict rework swap
    /// is blocked — no label changes occur.
    /// </summary>
    [Fact]
    public async Task Scenario1b_ConflictedPr_LinkedIssueHasWontDo_NoLabelChange()
    {
        // Arrange
        var ct = CancellationToken.None;
        Fixture.IssueProvider.Issues.Add(MakeIssue("102", AgentLabels.WontDo));
        Fixture.RepositoryProvider.PrMergeability[102] = PrMergeabilityStatus.Conflicted;
        Fixture.RepositoryProvider.PrLinkedIssues[102] = new List<string> { "102" };

        var pr = MakePr(102, "feature/auto-102-fix", labels: new[] { AgentLabels.Done });
        var agentDonePrs = new[] { pr };

        var svc = GetHousekeepingService();

        // Act
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: no label changes for issue 102 (wont-do blocks rework)
        var changes = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "102")
            .ToList();
        Assert.Empty(changes);
    }

    // ── Scenario 2 — Branch updates ───────────────────────────────────────

    /// <summary>
    /// Scenario 2a: three behind-base PRs with concurrency limit 1 — exactly one branch
    /// update is triggered per cycle.
    /// </summary>
    [Fact]
    public async Task Scenario2a_ThreeBehindPrs_ConcurrencyLimit1_ExactlyOneUpdateTriggered()
    {
        // Arrange — PR numbers 201-203, all Behind
        var ct = CancellationToken.None;
        Fixture.RepositoryProvider.PrMergeability[201] = PrMergeabilityStatus.Behind;
        Fixture.RepositoryProvider.PrMergeability[202] = PrMergeabilityStatus.Behind;
        Fixture.RepositoryProvider.PrMergeability[203] = PrMergeabilityStatus.Behind;

        var pr201 = MakePr(201, "feature/auto-201-work");
        var pr202 = MakePr(202, "feature/auto-202-work");
        var pr203 = MakePr(203, "feature/auto-203-work");
        var agentDonePrs = new[] { pr201, pr202, pr203 };

        var svc = GetHousekeepingService();

        // Act
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,   // limit = 1
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: exactly one update triggered (concurrency slot full after first)
        // TODO: also assert that the triggered PR is one of {201, 202, 203} to make the
        // assertion self-contained if BranchUpdateCalls leaks state from a prior test run.
        Assert.Single(Fixture.RepositoryProvider.BranchUpdateCalls);
    }

    /// <summary>
    /// Scenario 2b: a draft PR is skipped — it must never appear in BranchUpdateCalls.
    /// </summary>
    [Fact]
    public async Task Scenario2b_DraftPr_IsSkippedForBranchUpdate()
    {
        // Arrange — PR 204 is draft; PR 205 is normal Behind
        var ct = CancellationToken.None;
        Fixture.RepositoryProvider.PrMergeability[204] = PrMergeabilityStatus.Behind;
        Fixture.RepositoryProvider.PrMergeability[205] = PrMergeabilityStatus.Behind;

        var pr204 = MakePr(204, "feature/auto-204-work", isDraft: true);
        var pr205 = MakePr(205, "feature/auto-205-work");
        var agentDonePrs = new[] { pr204, pr205 };

        var svc = GetHousekeepingService();

        // Act — high limit so the non-draft PR gets a slot
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 10,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: draft PR 204 was NOT updated; normal PR 205 was
        Assert.DoesNotContain(204, Fixture.RepositoryProvider.BranchUpdateCalls);
        Assert.Contains(205, Fixture.RepositoryProvider.BranchUpdateCalls);
    }

    /// <summary>
    /// Scenario 2c: a PR whose branch has an active pipeline run is skipped.
    /// The active-branches endpoint (<c>GET /api/pipeline-runs/active-branches</c>) filters
    /// with <c>WhereActive()</c>, which matches <c>Status == Dispatched || Status == Running</c>.
    /// Inserting a <c>Dispatched</c> WorkItem with <c>BranchName = "feature/auto-207-work"</c>
    /// causes the endpoint to return that branch, so <c>HousekeepingService</c> skips PR 207.
    /// <para>
    /// NOTE: <c>Pending</c> is NOT included by <c>WhereActive()</c> — only <c>Dispatched</c>
    /// and <c>Running</c> statuses appear in the active-branches response.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario2c_PrWithActiveRun_IsSkippedForBranchUpdate()
    {
        // Arrange — PR 206 is Behind (no active run), PR 207 is Behind (active run on its branch)
        var ct = CancellationToken.None;
        Fixture.RepositoryProvider.PrMergeability[206] = PrMergeabilityStatus.Behind;
        Fixture.RepositoryProvider.PrMergeability[207] = PrMergeabilityStatus.Behind;

        // Insert a Dispatched WorkItem with BranchName matching PR 207's branch.
        // GET /api/pipeline-runs/active-branches uses WhereActive() which matches
        // Status == Dispatched || Status == Running — Pending is excluded. Using Dispatched
        // here ensures the branch appears in the active-branches response, causing
        // HousekeepingService to skip PR 207.
        var branchName207 = "feature/auto-207-work";
        var workItemId207 = Guid.NewGuid();
        // TODO: verify that Fixture.DbContextFactory.CreateDbContext() returns an independent
        // (non-pooled) DbContext instance. If the factory returns a pooled instance and the
        // await using block disposes it while ExecuteAsync holds an indirect reference, an
        // ObjectDisposedException could occur. The current assumption is that each call creates
        // a fresh context; confirm this if the factory implementation ever changes.
        await using var db207 = Fixture.DbContextFactory.CreateDbContext();
        db207.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId207,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "207",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            ProjectId = Guid.Parse(WellKnownIds.DefaultProjectId),
            BranchName = branchName207,
        });
        await db207.SaveChangesAsync(ct); // TODO: pass ct consistently; was missing CancellationToken
        // TODO: workItemId207 is declared but never read after insertion. Add an assertion on
        // the inserted row or remove the variable if the identifier is not needed for verification.
        // TODO: this WorkItem row is cleaned up by ResetAllAsync (which truncates WorkItems), but
        // if ResetAllAsync is ever changed to skip the WorkItems table, this row would persist and
        // cause branches named "feature/auto-207-work" to be incorrectly skipped in later tests.

        var pr206 = MakePr(206, "feature/auto-206-work");
        var pr207 = MakePr(207, branchName207);
        var agentDonePrs = new[] { pr206, pr207 };

        var svc = GetHousekeepingService();

        // Act
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 10,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: PR 207 (active run on its branch) was skipped; PR 206 was updated
        Assert.DoesNotContain(207, Fixture.RepositoryProvider.BranchUpdateCalls);
        Assert.Contains(206, Fixture.RepositoryProvider.BranchUpdateCalls);
    }

    /// <summary>
    /// Scenario 2d: after a PR enters in-flight, a second ExecuteAsync call within the cooldown
    /// window does not re-trigger the same PR (in-flight guard fires first).
    /// </summary>
    [Fact]
    public async Task Scenario2d_InFlightPr_NotRetriggeredOnSecondCycle()
    {
        // Arrange — PR 208 is Behind; limit = 1 so it enters in-flight after first call
        var ct = CancellationToken.None;
        Fixture.RepositoryProvider.PrMergeability[208] = PrMergeabilityStatus.Behind;

        var pr208 = MakePr(208, "feature/auto-208-work");
        var agentDonePrs = new[] { pr208 };

        var svc = GetHousekeepingService();

        // First call — PR 208 should be triggered
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        Assert.Single(Fixture.RepositoryProvider.BranchUpdateCalls);
        Assert.Contains(208, Fixture.RepositoryProvider.BranchUpdateCalls);

        // Second call — PR 208 is still Behind and still in-flight; in-flight guard skips it
        // (the concurrency slot is occupied, same PR number). Ensure TriggerCooldown is large
        // so the cooldown guard would also fire if the in-flight guard were absent.
        // TODO: svc.TriggerCooldown = TimeSpan.FromHours(1) is a no-op here because ExecuteAsync
        // overwrites TriggerCooldown internally via Math.Max(1, triggerCooldownMinutes) (1 minute).
        // Both the in-flight guard and the cooldown guard (1 min elapsed < 1 min window) are
        // simultaneously active, so this test does not cleanly isolate the in-flight guard.
        // To isolate it properly: advance svc.UtcNow past the 1-minute cooldown before the second
        // call so only the in-flight guard fires.
        svc.TriggerCooldown = TimeSpan.FromHours(1);
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: still only 1 update call total — in-flight suppression prevented re-trigger
        Assert.Single(Fixture.RepositoryProvider.BranchUpdateCalls);
    }

    // ── Scenario 3 — Stale branches ───────────────────────────────────────

    /// <summary>
    /// Scenario 3: stale branch cleanup.
    /// <list type="bullet">
    ///   <item>Branch 301 — has open PR in <c>agentDonePrs</c> → protected</item>
    ///   <item>Branch 312 — linked issue has <c>agent:in-progress</c> → protected by active label</item>
    ///   <item>Branch 313 — no PR, no active label → deleted</item>
    ///   <item>Branch 314 — no PR, no active label → deleted</item>
    /// </list>
    /// Branch names must use the <c>feature/auto-{issueId}-{slug}</c> format so
    /// <c>StaleBranchCleaner.ExtractIssueId</c> can parse them. Issue IDs must exist in
    /// <c>Fixture.IssueProvider.Issues</c> or <c>GetIssueAsync</c> throws and the branch is skipped.
    /// The open-PR protection uses the <c>agentDonePrs</c> argument, NOT
    /// <c>Fixture.RepositoryProvider.PullRequests</c>.
    /// </summary>
    [Fact]
    public async Task Scenario3_StaleBranchCleanup_DeletesOnlyStaleBranches()
    {
        // Arrange
        var ct = CancellationToken.None;

        // Seed the issues that the branch names reference
        Fixture.IssueProvider.Issues.Add(MakeIssue("301")); // no active label → stale candidate
        Fixture.IssueProvider.Issues.Add(MakeIssue("312", AgentLabels.InProgress)); // protected by label
        Fixture.IssueProvider.Issues.Add(MakeIssue("313")); // stale
        Fixture.IssueProvider.Issues.Add(MakeIssue("314")); // stale

        // Populate agent branches — all using feature/auto-{issueId}-{slug} format
        Fixture.RepositoryProvider.AgentBranches.AddRange(new[]
        {
            "feature/auto-301-fix",   // protected by open PR in agentDonePrs
            "feature/auto-312-work",  // protected by agent:in-progress label
            "feature/auto-313-stale", // stale
            "feature/auto-314-stale", // stale
        });

        // Build agentDonePrs — only PR 301's branch is in this list (that's what protects it)
        var pr301 = MakePr(301, "feature/auto-301-fix", labels: new[] { AgentLabels.Done });
        Fixture.RepositoryProvider.PrMergeability[301] = PrMergeabilityStatus.UpToDate;

        var agentDonePrs = new[] { pr301 };

        var svc = GetHousekeepingService();

        // Act — branchCleanupEnabled = true, cleanupIntervalMinutes = 0 (always due)
        await svc.ExecuteAsync(
            new HousekeepingRequest
            {
                RepoProvider = Fixture.RepositoryProvider,
                RepoProviderId = "repo-e2e",
                IssueProvider = Fixture.IssueProvider,
                IssueProviderId = "issue-e2e",
                AgentDonePrs = agentDonePrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = 1,
                BranchCleanupEnabled = true,
                CleanupIntervalMinutes = 0,
                TriggerCooldownMinutes = 1
            },
            ct);

        // Assert: only the stale branches were deleted
        // TODO: this scenario does not cover the wasInputTruncated = true path. When input is
        // truncated, StaleBranchCleaner.RunBranchCleanupAsync silently skips all deletions to
        // avoid incorrectly pruning branches whose PRs weren't in the truncated list. That guard
        // has no E2E coverage; only unit tests in StaleBranchCleanerTests cover it.
        Assert.Equal(2, Fixture.RepositoryProvider.DeletedBranches.Count);
        Assert.Contains("feature/auto-313-stale", Fixture.RepositoryProvider.DeletedBranches);
        Assert.Contains("feature/auto-314-stale", Fixture.RepositoryProvider.DeletedBranches);

        // Assert: protected branches were NOT deleted
        Assert.DoesNotContain("feature/auto-301-fix", Fixture.RepositoryProvider.DeletedBranches);
        Assert.DoesNotContain("feature/auto-312-work", Fixture.RepositoryProvider.DeletedBranches);
    }

    // ── Scenario 4 — Orphaned labels ─────────────────────────────────────

    /// <summary>
    /// Scenario 4a: issue 401 has <c>agent:in-progress</c> with no active run →
    /// after the sweep it should have <c>agent:error</c>.
    /// Issue 402 has <c>agent:in-progress</c> with an active WorkItem → untouched.
    ///
    /// <para>
    /// The active-run check uses <c>_workItemClient.IsIssueDistributedAsync</c> (HTTP to the API,
    /// querying the Postgres WorkItems table). A <see cref="E2EFixture.InsertPendingWorkItemAsync"/>
    /// row with <c>IssueProviderConfigId = "issue-e2e"</c> causes it to return <c>true</c>.
    /// </para>
    ///
    /// <para>
    /// <c>SweepOnceForTestAsync</c> bypasses the 60-second grace period and background timer —
    /// it calls <c>RecoverOrphanedLabelsAsync</c> directly.
    /// </para>
    ///
    /// <para>
    /// A template with <c>IssueProviderId = "issue-e2e"</c> must be seeded so
    /// <c>OrphanedLabelRecoveryService</c> discovers the provider to scan.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario4a_OrphanedInProgress_WithNoActiveRun_BecomesAgentError()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // Seed a template so OrphanedLabelRecoveryService finds "issue-e2e" to scan
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "housekeeping-e2e-orphan-template",
            Name = "Housekeeping Orphan Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, ct);

        // Issue 401: agent:in-progress, no WorkItem in DB
        Fixture.IssueProvider.Issues.Add(MakeIssue("401", AgentLabels.InProgress));

        // Issue 402: agent:in-progress + active WorkItem
        Fixture.IssueProvider.Issues.Add(MakeIssue("402", AgentLabels.InProgress));
        await InsertPendingWorkItemAsync("402");

        // Act
        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // Assert 401: swapped to agent:error
        var adds401 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "401" && lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.Contains(AgentLabels.Error, adds401);
        // TODO: also assert that agent:in-progress was removed from issue 401. TrySwapToErrorAsync
        // calls TrySwapLabelAsync (add-before-remove), so both an add and a remove are recorded.
        // Missing the remove assertion means a bug that adds agent:error without removing
        // agent:in-progress (leaving the issue in a dual-label state) would not be caught here.

        // Assert 402: no label changes (active WorkItem protects it)
        var changes402 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "402")
            .ToList();
        Assert.Empty(changes402);
    }

    /// <summary>
    /// Scenario 4b: issue 403 has dual labels <c>agent:in-progress</c> + <c>agent:next</c>.
    /// The dual-label resolution in Pass 1 resolves to <c>agent:in-progress</c> (higher precedence
    /// per <see cref="AgentLabels.DualLabelResolutionPrecedence"/>: InProgress is index 7, Next is
    /// index 9), removing <c>agent:next</c>.
    /// </summary>
    [Fact]
    public async Task Scenario4b_DualLabelIssue_InProgressPlusNext_ResolvesToInProgress()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;

        // Seed a template so OrphanedLabelRecoveryService finds "issue-e2e" to scan
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = "housekeeping-e2e-dual-template",
            Name = "Housekeeping Dual-Label Template",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
        }, ct);

        // Issue 403: dual-label [agent:in-progress, agent:next], no active WorkItem
        Fixture.IssueProvider.Issues.Add(MakeIssue("403", AgentLabels.InProgress, AgentLabels.Next));

        // Act
        await Fixture.SchedulerFactory.OrphanedLabelRecovery.SweepOnceForTestAsync(ct);

        // Assert: agent:next was removed (lower precedence than agent:in-progress)
        var removes403 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "403" && !lc.Added)
            .Select(lc => lc.Label)
            .ToList();
        Assert.Contains(AgentLabels.Next, removes403);

        // Assert: agent:in-progress was NOT removed (it is the higher-precedence label to keep)
        Assert.DoesNotContain(AgentLabels.InProgress, removes403);
        // TODO: add a positive assertion that agent:in-progress is still present on issue 403
        // after the sweep (e.g. check Fixture.IssueProvider.Issues or the adds log). The current
        // DoesNotContain only checks the remove log — it would pass even if both labels were
        // removed and a third was added, or if agent:in-progress was removed and re-added.
    }

    // ── Scenario 5 — HousekeepingEnabled = false ─────────────────────────

    /// <summary>
    /// Scenario 5: when <c>HousekeepingEnabled = false</c> on the template, the pipeline loop
    /// gate in <c>PipelineLoopService.RunHousekeepingAsync</c> skips the template entirely —
    /// <c>ExecuteAsync</c> is never called. No label changes, no branch updates, no deletions.
    ///
    /// <para>
    /// This scenario tests the loop-layer gate, NOT <c>ExecuteAsync</c> directly.
    /// <c>HousekeepingEnabled</c> is checked in <c>RunHousekeepingAsync</c> before
    /// <c>ExecuteAsync</c> is invoked, so calling <c>ExecuteAsync</c> directly would bypass
    /// the gate and always run housekeeping regardless of the flag.
    /// </para>
    ///
    /// <para>
    /// The test calls <c>loopService.RunHousekeepingAsync</c> (internal, accessible via
    /// <c>InternalsVisibleTo</c>) with a manually-built <c>CycleSnapshot</c> whose sole template
    /// has <c>HousekeepingEnabled = false</c>, and provider caches seeded directly into
    /// <c>loopService._cacheManager</c>.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Scenario5_HousekeepingDisabledOnTemplate_NothingHappens()
    {
        // Arrange — seed PRs as in scenarios 1-2 so we can assert nothing changed
        var ct = CancellationToken.None;

        Fixture.IssueProvider.Issues.Add(MakeIssue("501", AgentLabels.Done));
        Fixture.RepositoryProvider.PrMergeability[501] = PrMergeabilityStatus.Conflicted;
        Fixture.RepositoryProvider.PrLinkedIssues[501] = new List<string> { "501" };

        Fixture.RepositoryProvider.PrMergeability[502] = PrMergeabilityStatus.Behind;
        Fixture.RepositoryProvider.PrMergeability[503] = PrMergeabilityStatus.Behind;

        var pr501 = MakePr(501, "feature/auto-501-fix", labels: new[] { AgentLabels.Done });
        var pr502 = MakePr(502, "feature/auto-502-work");
        var pr503 = MakePr(503, "feature/auto-503-work");
        var agentDonePrs = new List<PullRequestSummary> { pr501, pr502, pr503 };

        // Build the disabled template
        var disabledTemplate = new PipelineJobTemplate
        {
            Id = "hk-disabled-template",
            Name = "Disabled Housekeeping",
            IssueProviderId = "issue-e2e",
            RepoProviderId = "repo-e2e",
            Enabled = true,
            HousekeepingEnabled = false,   // ← the gate under test
        };

        // Build CycleSnapshot with the disabled template
        var project = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = new List<string> { disabledTemplate.Id },
        };
        var flattened = new List<(PipelineJobTemplate Template, PipelineProject Project)>
            { (disabledTemplate, project) };

        var snapshot = new PipelineLoopService.CycleSnapshot(
            Config: TestPipelineConfig.Default(),
            Projects: new[] { project },
            FlattenedTemplates: flattened,
            EnabledTemplates: new[] { disabledTemplate },
            PollableTemplates: new[] { disabledTemplate },
            TemplateLookup: new Dictionary<string, PipelineJobTemplate>
                { [disabledTemplate.Id] = disabledTemplate }.AsReadOnly(),
            ActiveIssueIdentifiers: new HashSet<(IssueIdentifier, ProviderConfigId)>(),
            ActiveDecompositionCount: 0);

        // Seed the provider caches directly on the loop service (same pattern as unit tests)
        var loopSvc = Fixture.SchedulerFactory.LoopService;
        loopSvc._cacheManager.RepoProviders["repo-e2e"] = Fixture.RepositoryProvider;
        loopSvc._cacheManager.IssueProviders["issue-e2e"] = Fixture.IssueProvider;

        // Build the agentDonePrQueues as the real loop would (keyed by template ID)
        var agentDonePrQueues = new Dictionary<string, List<PullRequestSummary>>
        {
            [disabledTemplate.Id] = agentDonePrs,
        };
        var agentDonePrTruncated = new Dictionary<string, bool>
        {
            [disabledTemplate.Id] = false,
        };

        // Act — call RunHousekeepingAsync through the loop service (exercises the gate)
        await loopSvc.RunHousekeepingAsync(snapshot, agentDonePrQueues, agentDonePrTruncated, ct);

        // Assert: no label changes on issue 501 (conflict rework was blocked)
        var labelChanges501 = Fixture.IssueProvider.LabelChanges
            .Where(lc => lc.Identifier == "501")
            .ToList();
        Assert.Empty(labelChanges501);

        // Assert: no branch updates (behind PRs 502-503 were not triggered)
        Assert.Empty(Fixture.RepositoryProvider.BranchUpdateCalls);

        // Assert: no branch deletions
        Assert.Empty(Fixture.RepositoryProvider.DeletedBranches);
        // TODO: the three absence assertions above (empty label changes, empty branch updates,
        // empty deletions) cannot distinguish "gate skipped correctly" from "RunHousekeepingAsync
        // crashed silently and returned without doing anything." An additional positive assertion
        // — e.g. a skip counter, a log entry, or a call recorder on the loop service — would
        // confirm the gate was reached and fired, rather than the method exiting early due to an
        // unhandled exception or unexpected early return.
    }
}
