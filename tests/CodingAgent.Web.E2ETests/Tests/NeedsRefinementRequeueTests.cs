using CodingAgent.Web.E2ETests.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.E2ETests.Tests;

/// <summary>
/// Headless E2E tests covering the staleness-detection logic for the needs-refinement requeue
/// flow (issue #3107).
///
/// <para>
/// The production staleness logic lives in
/// <c>DispatchInfrastructure.BuildIssueContextAsync</c> /
/// <c>DetectAnalysisStalenessAsync</c>. These tests exercise it end-to-end by seeding comments
/// into <see cref="Fakes.InMemoryIssueProvider"/> and asserting on
/// <see cref="CodingAgent.Pipeline.Models.JobAssignmentMessage.ForceRefreshAnalysis"/> and
/// <see cref="CodingAgent.Pipeline.Models.JobAssignmentMessage.StalenessSignal"/> after the
/// assignment is delivered to a <see cref="FakeAgentClient"/>.
/// </para>
///
/// <para>
/// All five scenarios are headless (no Playwright): they extend <see cref="HeadlessE2ETestBase"/>
/// and assert purely on API/DB state.
/// </para>
/// </summary>
[Trait("Category", "E2E")]
[Collection(E2ECollection.Name)]
public sealed class NeedsRefinementRequeueTests : HeadlessE2ETestBase
{
    public NeedsRefinementRequeueTests(E2EFixture fixture) : base(fixture) { }

    // ── Shared setup helpers ──────────────────────────────────────────────

    /// <summary>
    /// Seeds the minimal template and agent profile required for <see cref="DispatchIssueAsync"/>
    /// to succeed. Uses a unique label per call so agents do not cross-match across scenarios.
    /// </summary>
    private async Task SeedTemplateAndProfileAsync(string agentLabel)
    {
        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id = $"template-staleness-{agentLabel}",
            Name  = $"Staleness Test Template ({agentLabel})",
            IssueProviderId = "issue-e2e",
            RepoProviderId  = "repo-e2e",
            Enabled = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id             = $"profile-staleness-{agentLabel}",
            DisplayName    = $"Staleness Profile ({agentLabel})",
            MatchLabels    = new[] { agentLabel },
            AgentProviderConfigId = "agent-e2e",
            Enabled        = true
        }, CancellationToken.None);
    }

    // ── Scenario 1 ───────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 1 — Re-queue after rejection.
    /// Issue 42 has an analysis comment at T1 and a gate-rejection comment at T2 > T1.
    /// The assignment must carry ForceRefreshAnalysis=true, StalenessSignal="gate_rejection".
    /// </summary>
    [Fact]
    public async Task RequeueAfterRejection_ForcesFreshAnalysis()
    {
        const string agentLabel = "staleness-s1";
        await SeedTemplateAndProfileAsync(agentLabel);

        // TODO [WARNING]: Scenarios 1–4 all use Identifier="42". Isolation relies on
        // ResetAllAsync() (called by HeadlessE2ETestBase.InitializeAsync) clearing
        // InMemoryIssueProvider.Issues between tests. The [Collection(E2ECollection.Name)]
        // attribute serialises execution, so there is no collision in practice. Consider using
        // distinct identifiers per scenario ("42-s1", "42-s2", …) to make isolation explicit
        // and independent of the collection's serialisation guarantee.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier  = "42",
            Title       = "Re-queue after rejection test",
            Description = "## Requirements\nFix the thing.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels      = new[] { "agent:next" }
        });

        // T1: analysis comment — older
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_001L,
            body:            $"{CommentMarkers.AnalysisHeader}\n## Analysis content",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-30));

        // T2: gate-rejection comment — newer than analysis
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_002L,
            body:            $"{CommentMarkers.GateRejection}\nIssue needs more context.",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-10));

        await using var fakeAgent = new FakeAgentClient($"staleness-agent-{agentLabel}", agentLabel);
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await DispatchIssueAsync("42");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(assignment.ForceRefreshAnalysis,
            "Gate rejection newer than analysis must force a fresh analysis");
        Assert.Equal("gate_rejection", assignment.StalenessSignal);
    }

    // ── Scenario 2 ───────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 2 — No rejection.
    /// Only an analysis comment exists; no rejection comment.
    /// The assignment must carry ForceRefreshAnalysis=false and ExistingAnalysis != null.
    /// </summary>
    [Fact]
    public async Task NoRejection_CarriesExistingAnalysis()
    {
        const string agentLabel = "staleness-s2";
        await SeedTemplateAndProfileAsync(agentLabel);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier  = "42",
            Title       = "No rejection test",
            Description = "## Requirements\nFix the thing.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels      = new[] { "agent:next" }
        });

        // Only an analysis comment — no rejection comment
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_001L,
            body:            $"{CommentMarkers.AnalysisHeader}\n## Existing analysis",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-30));

        await using var fakeAgent = new FakeAgentClient($"staleness-agent-{agentLabel}", agentLabel);
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await DispatchIssueAsync("42");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(assignment.ForceRefreshAnalysis,
            "No rejection comment must not force a fresh analysis");
        Assert.Null(assignment.StalenessSignal);
        Assert.NotNull(assignment.ExistingAnalysis);
        // TODO [WARNING]: Only asserts the AnalysisHeader marker is present, not the specific body
        // content seeded above ("## Existing analysis"). If ExistingAnalysis were populated from any
        // comment containing the marker (e.g. stale fixture state), this assertion would still pass.
        // Consider adding: Assert.Contains("## Existing analysis", assignment.ExistingAnalysis)
        Assert.Contains(CommentMarkers.AnalysisHeader, assignment.ExistingAnalysis);
    }

    // ── Scenario 3 ───────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 3 — Old rejection.
    /// The gate-rejection comment (T1) is OLDER than the analysis comment (T2).
    /// The assignment must carry ForceRefreshAnalysis=false — rejection predates analysis.
    /// </summary>
    [Fact]
    public async Task OldRejection_NoForceRefresh()
    {
        const string agentLabel = "staleness-s3";
        await SeedTemplateAndProfileAsync(agentLabel);

        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier  = "42",
            Title       = "Old rejection test",
            Description = "## Requirements\nFix the thing.\n\n## Acceptance Criteria\n- [ ] Done",
            Labels      = new[] { "agent:next" }
        });

        // T1: gate-rejection comment — older than analysis
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_001L,
            body:            $"{CommentMarkers.GateRejection}\nOld rejection reason",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-60));

        // T2: analysis comment — newer than rejection (user refined and re-ran)
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_002L,
            body:            $"{CommentMarkers.AnalysisHeader}\n## Fresh analysis after refinement",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-10));

        await using var fakeAgent = new FakeAgentClient($"staleness-agent-{agentLabel}", agentLabel);
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await DispatchIssueAsync("42");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(assignment.ForceRefreshAnalysis,
            "Rejection older than the analysis comment must not force a refresh");
        Assert.Null(assignment.StalenessSignal);
    }

    // ── Scenario 4 ───────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 4 — Body changed.
    /// The analysis comment embeds a hash of the original description.
    /// The issue now carries a different description (user edited it after analysis).
    /// The assignment must carry ForceRefreshAnalysis=true, StalenessSignal="body_changed".
    /// </summary>
    [Fact]
    public async Task BodyChanged_ForcesFreshAnalysis()
    {
        const string agentLabel          = "staleness-s4";
        const string originalDescription = "## Original requirements\nBefore the user edited.";
        const string changedDescription  = "## Updated requirements\nUser edited the description after analysis.";

        await SeedTemplateAndProfileAsync(agentLabel);

        // Issue carries the *current* (changed) description
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier  = "42",
            Title       = "Body changed test",
            Description = changedDescription,
            Labels      = new[] { "agent:next" }
        });

        // Analysis comment embeds hash of the *original* description
        var originalHash = AnalysisBodyHash.Compute(originalDescription);
        Fixture.IssueProvider.SeedComment(
            issueIdentifier: "42",
            commentId:       9_000_000_001L,
            body:            $"{CommentMarkers.AnalysisHeader}\n## Prior analysis\n" +
                             $"<!-- agent:analysis-body-hash:{originalHash} -->",
            author:          "bot",
            createdAt:       DateTime.UtcNow.AddMinutes(-30));

        await using var fakeAgent = new FakeAgentClient($"staleness-agent-{agentLabel}", agentLabel);
        await fakeAgent.ConnectAsync(AgentHubUrl, Fixture.ApiKey);

        await DispatchIssueAsync("42");

        var assignment = await fakeAgent.JobAssigned.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(assignment.ForceRefreshAnalysis,
            "Issue body changed since analysis was written must force a fresh analysis");
        Assert.Equal("body_changed", assignment.StalenessSignal);
    }

    // ── Scenario 5 ───────────────────────────────────────────────────────

    /// <summary>
    /// Scenario 5 — Closed-loop skip.
    /// An issue labelled both agent:next and agent:needs-refinement must NOT be dispatched
    /// by the closed-loop <see cref="PipelineLoopService"/>. Manual dispatch is NOT under test
    /// here; only the loop-based dispatch path is exercised.
    /// </summary>
    [Fact]
    public async Task NeedsRefinementLabel_ClosedLoopSkips()
    {
        // Seed a short poll interval so the loop runs quickly in CI
        var config = await Fixture.ConfigStore.LoadPipelineConfigAsync(CancellationToken.None);
        await Fixture.ConfigStore.SavePipelineConfigAsync(config with
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(1)
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveTemplateAsync(WellKnownIds.DefaultProjectId, new PipelineJobTemplate
        {
            Id              = "template-staleness-s5",
            Name            = "Staleness Test Template (s5)",
            IssueProviderId = "issue-e2e",
            RepoProviderId  = "repo-e2e",
            Enabled         = true
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveAgentProfileAsync(new AgentProfile
        {
            Id                    = "profile-staleness-s5",
            DisplayName           = "Staleness Profile (s5)",
            MatchLabels           = new[] { "staleness-s5" },
            AgentProviderConfigId = "agent-e2e",
            Enabled               = true
        }, CancellationToken.None);

        // Issue has BOTH agent:next and agent:needs-refinement — the loop must skip it.
        // The issue also carries "staleness-s5" so that the seeded agent profile (MatchLabels=["staleness-s5"])
        // WOULD match it during PrepareDistributionRequestAsync if not for the needs-refinement guard.
        // Without this label, no profile matches the issue regardless, and the test would pass vacuously
        // even if the agent:needs-refinement skip logic in DispatchScheduler were completely removed.
        Fixture.IssueProvider.Issues.Add(new IssueDetail
        {
            Identifier  = "42",
            Title       = "Needs refinement skip test",
            Description = "## Requirements\nVague requirements.\n\n## Acceptance Criteria\n- [ ] TBD",
            Labels      = new[] { "agent:next", "agent:needs-refinement", "staleness-s5" }
        });

        var loopService = Fixture.SchedulerFactory.Services.GetRequiredService<PipelineLoopService>();
        // TODO [WARNING]: PipelineLoopService is a singleton. StopLoop() in the finally block
        // only sets _stopRequested — it does not await loop task completion. If the loop's
        // DelayOrStop is mid-sleep when StopLoop fires, the background task is still running
        // when the next test's InitializeAsync/ResetAllAsync clears provider state, which can
        // cause the in-flight loop iteration to call into reset fakes and produce spurious
        // failures in subsequent tests. Matches the same pattern in ClosedLoopDispatchTests and
        // EpicDecompositionTests; a shared fix would be to await IsLoopActive becoming false
        // in E2EFixture.ResetAllAsync before proceeding (already attempted with a 10s timeout).
        try
        {
            var started = await loopService.StartLoopAsync();
            Assert.True(started, "StartLoopAsync should return true with valid template config");

            // Wait for at least one cycle to complete (up to 15s)
            // TODO [WARNING]: StatusMessage is a plain auto-property updated inside a lock() in
            // PipelineLoopService but read here without any memory barrier. On Arm64 release builds
            // the JIT may cache the field value in a register and never see the updated string.
            // Using Volatile.Read or making the backing field volatile would guarantee visibility.
            // In practice the 50 ms poll interval is sufficient on x64, but this is a latent
            // correctness issue for non-x64 CI environments. Matches the same pattern in
            // ClosedLoopDispatchTests and EpicDecompositionTests.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!loopService.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
                   && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.Contains("Cycle complete", loopService.StatusMessage, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            loopService.StopLoop();
        }

        // Assert: no WorkItem was created for this issue
        var pending = await Fixture.WorkItems.GetPendingAsync(maxResults: 50, ct: CancellationToken.None);
        var active  = await Fixture.WorkItems.GetActiveAsync(olderThanSeconds: -3600, ct: CancellationToken.None);

        // TODO [WARNING]: This negative assertion only checks pending/active WorkItems; a WorkItem
        // that was created and immediately completed/failed (moved to a terminal state) would not
        // be returned by GetPendingAsync or GetActiveAsync, so the assertion would still pass.
        // A stronger check would query the database directly via Fixture.DbContextFactory or
        // assert via WaitForHistoryAsync with a short timeout to confirm no run was created at all.
        // TODO [WARNING]: The assertion confirms no WorkItem was created, but does not verify that
        // the loop actually evaluated issue "42" at all. If the loop processed zero issues and still
        // emitted "Cycle complete", the skip logic never ran and this test passes trivially.
        // A stronger assertion would verify the issue was seen and skipped (e.g. a SkippedIssues
        // counter on PipelineLoopService, or a ListOpenIssuesAsync call count on the fake provider).
        Assert.False(
            pending.Any(w => w.IssueIdentifier == "42") || active.Any(w => w.IssueIdentifier == "42"),
            "Issue labelled agent:needs-refinement must be skipped by the closed loop and produce no WorkItem");
    }
}
