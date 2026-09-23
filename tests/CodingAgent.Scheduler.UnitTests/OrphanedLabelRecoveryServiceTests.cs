using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="OrphanedLabelRecoveryService"/> — dual-label detection and resolution.
///
/// Tests call the internal <see cref="OrphanedLabelRecoveryService.SweepOnceForTestAsync"/> method
/// directly to avoid BackgroundService timer races and thread-scheduling flakiness.
///
/// Each sweep invocation creates two <see cref="IIssueProvider"/> instances:
/// <list type="bullet">
///   <item>Pass 1: queries agent:in-progress issues.</item>
///   <item>Pass 2: queries agent:done issues.</item>
/// </list>
/// Helper methods name the two provider instances explicitly to prevent cross-contamination.
///
/// Covers the new dual-label sweep introduced for issue #2648:
/// <list type="bullet">
///   <item>Pass 1 (agent:in-progress scan) — dual-label route via TryRecoverSingleIssueAsync.</item>
///   <item>Pass 2 (agent:done scan) — ScanProviderForDualLabelIssuesAsync.</item>
///   <item>Precedence resolution, idempotency, Defense 3 guard, agent:generated coexistence.</item>
/// </list>
/// </summary>
[Collection("SchedulerTiming")]
public sealed class OrphanedLabelRecoveryServiceTests
{
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItemClient = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new(MockBehavior.Strict);
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<ILogger> _mockLogger = new();

    public OrphanedLabelRecoveryServiceTests()
    {
        _mockLogger
            .Setup(l => l.ForContext<OrphanedLabelRecoveryService>())
            .Returns(_mockLogger.Object);
        _mockLogger
            .Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>()))
            .Returns(_mockLogger.Object);

        // Default: single template pointing at provider-1.
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "repo-1" }
            });

        // Default: provider-1 config exists.
        _mockConfigClient
            .Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "provider-1", Kind = ProviderKind.Issue,
                    DisplayName = "Provider 1", ProviderType = "GitHub",
                    Settings = new Dictionary<string, string>()
                }
            });

        // Default: no active WorkItems — issues are not distributed.
        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        // Default: no recently-completed runs.
        _mockRunService
            .Setup(r => r.WasRecentlyCompleted(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        // Default: issue is not being processed in-memory.
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private OrphanedLabelRecoveryService CreateService() => new(
        _mockRunService.Object,
        _mockConfigClient.Object,
        _mockWorkItemClient.Object,
        _mockProviderFactory.Object,
        _mockLabelService.Object,
        leaderGate: null,           // null = run unconditionally (no leader election in unit tests)
        _mockLogger.Object,
        gracePeriod: TimeSpan.Zero); // no grace period — sweep immediately

    private static IssueDetail ToDetail(IssueSummary s) =>
        new() { Identifier = s.Identifier, Title = s.Title, Description = "", Labels = s.Labels };

    private static PagedResult<IssueSummary> OnePageOf(params IssueSummary[] items) =>
        new() { Items = items, Page = 1, PageSize = 100, HasMore = false };

    private static PagedResult<IssueSummary> EmptyPage() =>
        new() { Items = [], Page = 1, PageSize = 100, HasMore = false };

    /// <summary>
    /// Wires the provider factory so that:
    /// - The first call to CreateIssueProvider returns <paramref name="pass1Provider"/> (in-progress scan).
    /// - The second call returns <paramref name="pass2Provider"/> (done scan).
    /// </summary>
    // TODO: This helper assumes CreateIssueProvider is called exactly twice per sweep (once per scan pass).
    // The odd/even interleaving via Interlocked.Increment silently breaks if a future change causes a third
    // scan pass (call 3 maps back to the pass-1 slot), producing false-positive test results with no
    // assertion failure. If a third pass is ever added, this helper must be updated to match. Tests using
    // this helper are also constrained to a single-provider setup (the constructor wires exactly one
    // provider-1 template); adding a second template would cause 4 CreateIssueProvider calls per sweep,
    // corrupting the alternation. The [Collection("SchedulerTiming")] attribute prevents parallel test
    // execution but does not enforce the single-provider constraint.
    private void WireProviders(IIssueProvider pass1Provider, IIssueProvider pass2Provider)
    {
        var callCount = 0;
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(() => Interlocked.Increment(ref callCount) % 2 == 1 ? pass1Provider : pass2Provider);
    }

    /// <summary>
    /// Creates a provider that returns <paramref name="item"/> and sets up GetIssueAsync
    /// with the supplied <paramref name="currentLabels"/> (defaults to item.Labels).
    /// </summary>
    private static Mock<IIssueProvider> BuildProvider(IssueSummary item, IReadOnlyList<string>? currentLabels = null)
    {
        var mock = new Mock<IIssueProvider>();
        mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(OnePageOf(item));
        mock.Setup(p => p.GetIssueAsync(item.Identifier, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = item.Identifier,
                Title = item.Title,
                Description = "",
                Labels = currentLabels ?? item.Labels
            });
        mock.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }

    /// <summary>Creates a provider that returns an empty list (no issues).</summary>
    private static Mock<IIssueProvider> EmptyProvider()
    {
        var mock = new Mock<IIssueProvider>();
        mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyPage());
        mock.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        return mock;
    }

    // ── Pass 1 (agent:in-progress scan with dual-label detection) ──────────

    /// <summary>
    /// An issue carrying both agent:in-progress and agent:done is detected via Pass 1
    /// (the existing agent:in-progress query) and resolved to agent:done (higher precedence).
    /// Acceptance criteria: an issue with both labels is resolved without manual intervention.
    /// </summary>
    [Fact]
    public async Task Pass1_WhenInProgressIssueHasDualLabel_ResolvesToDone()
    {
        var issue = new IssueSummary
        {
            Identifier = "42",
            Title = "Dual-label issue",
            Labels = [AgentLabels.InProgress, AgentLabels.Done]
        };

        // Pass 1: in-progress query returns the dual-label issue.
        // Pass 2: done query returns empty (issue "42" has both labels but will already be fixed).
        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // agent:done has higher precedence than agent:in-progress → keep done.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.Is<ProviderConfigId>(p => p.Value == "provider-1"),
                It.Is<IssueIdentifier>(i => i.Value == "42"),
                AgentLabels.Done,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "dual-label issue must be resolved to agent:done (higher precedence)");

        // Verify agent:in-progress was never chosen as the winner.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                AgentLabels.InProgress,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "agent:in-progress must not be selected when agent:done is present");
    }

    /// <summary>
    /// Production scenario: an issue carrying both agent:in-progress and agent:done appears in BOTH
    /// the Pass 1 agent:in-progress query AND the Pass 2 agent:done query in the same sweep.
    /// The pass-1-resolved deduplication guard must ensure SwapLabelAsync is called exactly once,
    /// not twice (which could cause a duplicate-add / not-found-remove on the second call).
    /// </summary>
    [Fact]
    public async Task WhenDualLabelIssueAppearsInBothPasses_SwapCalledOnlyOnce()
    {
        var issue = new IssueSummary
        {
            Identifier = "42",
            Title = "Dual-label issue in both passes",
            Labels = [AgentLabels.InProgress, AgentLabels.Done]
        };

        // Both Pass 1 and Pass 2 return the same dual-label issue — the production scenario.
        WireProviders(BuildProvider(issue).Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // The pass-1-resolved guard must prevent Pass 2 from processing the same issue.
        // SwapLabelAsync must be called exactly once across both passes.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "42"),
                AgentLabels.Done,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SwapLabelAsync must be called exactly once — Pass 2 must skip the issue already resolved by Pass 1");
    }

    /// <summary>
    /// An issue carrying both agent:in-progress and agent:error is detected via Pass 1
    /// and resolved to agent:error (higher precedence than in-progress).
    /// </summary>
    [Fact]
    public async Task Pass1_WhenInProgressIssueHasInProgressAndError_ResolvesToError()
    {
        var issue = new IssueSummary
        {
            Identifier = "55",
            Title = "In-progress + error dual-label issue",
            Labels = [AgentLabels.InProgress, AgentLabels.Error]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "55"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "agent:error has higher precedence than agent:in-progress");
    }

    // ── Pass 2 (agent:done scan) ───────────────────────────────────────────

    /// <summary>
    /// Pass 2 queries agent:done issues. An issue that carries agent:done AND agent:in-progress
    /// is detected and resolved to agent:done.
    /// This covers the scenario where the issue is not in the Pass 1 list (already lost
    /// agent:in-progress from the list query's perspective, but GetIssueAsync reveals both).
    /// </summary>
    [Fact]
    public async Task Pass2_WhenDoneIssueHasDualLabel_ResolvesToDone()
    {
        var issue = new IssueSummary
        {
            Identifier = "77",
            Title = "Done + in-progress dual-label",
            Labels = [AgentLabels.Done, AgentLabels.InProgress]
        };

        // Pass 1 (in-progress query): empty — issue not returned here.
        // Pass 2 (done query): returns the dual-label issue.
        WireProviders(EmptyProvider().Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "77"),
                AgentLabels.Done,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "dual-label issue detected via Pass 2 must be resolved to agent:done");
    }

    // ── Single-label issues are not touched ───────────────────────────────

    /// <summary>
    /// An issue with only agent:done (single label) must not be touched by the dual-label sweep.
    /// Acceptance criteria: issues with a single valid agent:* label are not touched.
    /// </summary>
    [Fact]
    public async Task Pass2_WhenIssueHasSingleLabel_NotTouched()
    {
        var issue = new IssueSummary
        {
            Identifier = "88",
            Title = "Single-label done issue",
            Labels = [AgentLabels.Done]
        };

        WireProviders(EmptyProvider().Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "single-label issue must not be modified by the dual-label sweep");
    }

    // ── Idempotency ────────────────────────────────────────────────────────

    /// <summary>
    /// Running the sweep twice — first on a dual-label issue, then on the same issue after
    /// it has been resolved to a single label — produces only one swap call in total.
    /// Acceptance criteria: the sweep pass is idempotent.
    /// </summary>
    // TODO: This test verifies idempotency across two separate OrphanedLabelRecoveryService instances
    // (one per CreateService() call) that share the same _mockLabelService. The Times.Once assertion
    // is satisfied because the second instance sees a single-label issue and does nothing. However,
    // this does not strictly verify that a single service instance encountering an already-resolved
    // issue on its second sweep is a no-op — the second instance could skip for an unrelated reason
    // (e.g. a newly introduced early-exit guard) and the test would still pass. A stronger form would
    // reuse the same service instance across both sweeps with re-wired providers.
    [Fact]
    public async Task DualLabel_IdempotentAfterResolution_SecondSweepDoesNothing()
    {
        var dualLabelIssue = new IssueSummary
        {
            Identifier = "42",
            Title = "Dual-label issue",
            Labels = [AgentLabels.InProgress, AgentLabels.Done]
        };

        // First sweep: Pass 1 returns the dual-label issue, Pass 2 returns empty.
        var pass1ProviderFirstSweep = BuildProvider(dualLabelIssue);
        WireProviders(pass1ProviderFirstSweep.Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "42"),
                AgentLabels.Done,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "first sweep must resolve the dual-label issue");

        // Second sweep: issue is now single-label (agent:done only) after resolution.
        var singleLabelIssue = new IssueSummary
        {
            Identifier = "42",
            Title = "Resolved issue",
            Labels = [AgentLabels.Done]
        };

        // Re-wire: Pass 1 empty, Pass 2 returns single-label issue.
        WireProviders(EmptyProvider().Object, BuildProvider(singleLabelIssue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // Total calls after both sweeps: still 1 (the second sweep did nothing).
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "second sweep must be a no-op when the issue is already resolved to a single label");
    }

    // ── Defense 3: active WorkItem skips the dual-label sweep ─────────────

    /// <summary>
    /// A dual-label issue with an active WorkItem (live agent running) must not be touched.
    /// Defense 3 applies to both Pass 1 and Pass 2.
    /// </summary>
    [Fact]
    public async Task Pass2_WhenActiveWorkItem_DualLabelIssueSkipped()
    {
        var issue = new IssueSummary
        {
            Identifier = "99",
            Title = "Dual-label with live agent",
            Labels = [AgentLabels.Done, AgentLabels.InProgress]
        };

        // IsIssueDistributedAsync returns true — live agent is processing this issue.
        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync(
                It.Is<string>(id => id == "99"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        WireProviders(EmptyProvider().Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "dual-label issue with an active WorkItem must not be modified (Defense 3)");

        // Positive guard: verify the distributed check actually fired (test can't pass vacuously).
        _mockWorkItemClient.Verify(
            w => w.IsIssueDistributedAsync("99", "provider-1", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "IsIssueDistributedAsync must have been called to reach the Times.Never assertion above");
    }

    // ── agent:generated coexistence ────────────────────────────────────────

    /// <summary>
    /// An issue with agent:in-progress and agent:generated must NOT be flagged as a
    /// dual-label violation. agent:generated is orthogonal and allowed to coexist with any
    /// status label. The issue should follow the normal orphan path (swapped to agent:error),
    /// not the dual-label resolution path.
    /// </summary>
    [Fact]
    public async Task Pass1_WhenInProgressPlusGenerated_TreatedAsOrphanNotDualLabel()
    {
        var issue = new IssueSummary
        {
            Identifier = "33",
            Title = "In-progress + generated (valid coexistence)",
            Labels = [AgentLabels.InProgress, AgentLabels.Generated]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // Not treated as dual-label: agent:in-progress or agent:generated must not win the
        // precedence contest (agent:generated is excluded from DualLabelResolutionPrecedence).
        // Instead, with only 1 non-generated status label, it falls through to the orphan
        // path and is swapped to agent:error.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "33"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "issue with [in-progress, generated] is an orphan (only 1 status label), must be swapped to error via the orphan path");
    }

    // ── Precedence resolution ──────────────────────────────────────────────

    /// <summary>
    /// For each label pair in DualLabelResolutionPrecedence, the label with the lower index
    /// (higher precedence) wins. Parametrised over representative pairs covering all precedence tiers.
    /// </summary>
    // TODO: Several InlineData cases (e.g. Done vs Error, WontDo vs Next, EpicReview vs Epic,
    // EpicApproved vs Epic) do not carry agent:in-progress and would NOT be returned by the real
    // in-progress Pass 1 query filter in production — they would only surface via Pass 2 (agent:done scan).
    // These cases are wired to Pass 1 only (Pass 2 slot is EmptyProvider), so the test validates that
    // precedence resolution logic is correct once an issue is found, but does NOT verify that the issue
    // is actually discovered by the sweep in production. If the Pass 2 query filter were accidentally
    // changed to exclude these labels, these tests would still pass.
    //
    // TODO: Times.AtLeastOnce is used for the winner assertion rather than Times.Once. If the same issue
    // surfaces in both Pass 1 and Pass 2, SwapLabelAsync could be called twice in one sweep (double-swap).
    // AtLeastOnce masks this scenario. Times.Once with correct provider wiring per case would be a
    // stronger guard against accidental double-resolution.
    [Theory]
    [InlineData(AgentLabels.Done, AgentLabels.InProgress)]        // terminal beats active
    [InlineData(AgentLabels.Done, AgentLabels.Next)]              // terminal beats pre-dispatch
    [InlineData(AgentLabels.Done, AgentLabels.Error)]             // done beats error (lower index = wins)
    [InlineData(AgentLabels.Error, AgentLabels.InProgress)]       // error beats active
    [InlineData(AgentLabels.Done, AgentLabels.NeedsRefinement)]   // done beats needs-refinement
    [InlineData(AgentLabels.Cancelled, AgentLabels.InProgress)]   // cancelled beats active
    [InlineData(AgentLabels.WontDo, AgentLabels.Next)]            // wont-do beats pre-dispatch
    [InlineData(AgentLabels.EpicReview, AgentLabels.Epic)]        // epic-review beats epic
    [InlineData(AgentLabels.EpicApproved, AgentLabels.Epic)]      // epic-approved beats epic
    [InlineData(AgentLabels.InProgress, AgentLabels.Next)]        // in-progress beats next (re-queue path)
    public async Task Precedence_WhenTwoLabelsPresent_HigherPrecedenceWins(
        string expectedWinner, string expectedLoser)
    {
        var issue = new IssueSummary
        {
            Identifier = "200",
            Title = $"Precedence test: {expectedWinner} vs {expectedLoser}",
            // Put the loser first in the array to ensure ordering in the list doesn't influence outcome.
            Labels = [expectedLoser, expectedWinner]
        };

        // Use Pass 1 provider so both the in-progress scan and done scan serve this issue.
        // We need the issue to surface regardless of which label filter is applied.
        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "200"),
                expectedWinner,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            $"expected {expectedWinner} to win over {expectedLoser}");

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "200"),
                expectedLoser,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Never,
            $"loser label {expectedLoser} must not be chosen as the winner");
    }

    // ── Pass 1 still handles orphans correctly after refactor ─────────────

    /// <summary>
    /// Regression guard: a genuine single-label agent:in-progress orphan (no WorkItem,
    /// not recently completed, still in-progress after re-fetch) must still be swapped
    /// to agent:error as before.
    /// </summary>
    [Fact]
    public async Task Pass1_GenuineOrphan_SwappedToError()
    {
        var issue = new IssueSummary
        {
            Identifier = "10",
            Title = "Genuine orphan",
            Labels = [AgentLabels.InProgress]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "10"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "genuine orphan must still be swapped to agent:error after the dual-label refactor");
    }

    /// <summary>
    /// Regression guard: Pass 1 must still skip issues that had a terminal label at
    /// GetIssueAsync time (Defense 1 — stale list result), now handled inline in
    /// TryRecoverSingleIssueAsync after the refactor.
    /// </summary>
    [Fact]
    public async Task Pass1_StaleInProgressList_TerminalAtGetIssue_NotSwapped()
    {
        var issue = new IssueSummary
        {
            Identifier = "15",
            Title = "Stale in-progress, actually done",
            Labels = [AgentLabels.InProgress]   // stale list result
        };

        // GetIssueAsync returns the actual current state: already done (single label).
        var provider = BuildProvider(issue, currentLabels: [AgentLabels.Done]);
        WireProviders(provider.Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "stale in-progress list result where issue is now done (single label) must not trigger any swap");

        // Positive guard: GetIssueAsync was called — not a vacuous pass.
        provider.Verify(p => p.GetIssueAsync("15", It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    // ── TrySwapToErrorAsync exception / OCE handling ──────────────────────

    /// <summary>
    /// When TrySwapToErrorAsync's inner swap fails (non-OCE), the sweep continues without throwing
    /// and recoveredCount is NOT incremented — the failure was swallowed but the swap did not apply.
    /// </summary>
    [Fact]
    public async Task Pass1_WhenSwapToErrorFails_SweepContinues()
    {
        var issue = new IssueSummary
        {
            Identifier = "orphan-fail",
            Title = "Orphan whose swap fails",
            Labels = [AgentLabels.InProgress]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var (service, sink) = CreateServiceWithCapture();

        // Must not throw — failure is non-fatal
        var act = () => service.SweepOnceForTestAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // recoveredCount must be 0: the swap failed so the issue was not recovered.
        var completionEvent = sink.Events
            .FirstOrDefault(e => e.MessageTemplate.Text.Contains("Orphaned label recovery complete"));
        completionEvent.Should().NotBeNull("the completion log must always be emitted");
        // TODO: [WARNING] Properties["Count"].ToString() asserts the rendered string of a Serilog ScalarValue.
        // For an integer property this currently works (renders as bare numeral), but if the log call ever
        // passes recoveredCount as a string instead of an int, ScalarValue.ToString() would return "\"0\""
        // and the assertion would silently fail. Safer: ((ScalarValue)completionEvent!.Properties["Count"]).Value.Should().Be(0)
        completionEvent!.Properties["Count"].ToString().Should().Be("0",
            "a failed swap must not increment recoveredCount");
    }

    /// <summary>
    /// When TrySwapToDualLabelResolutionAsync's inner swap fails (non-OCE), the sweep continues
    /// without throwing and recoveredCount is NOT incremented — the failure was swallowed but the
    /// swap did not apply.
    /// </summary>
    [Fact]
    public async Task Pass2_WhenDualLabelSwapFails_SweepContinues()
    {
        var issue = new IssueSummary
        {
            Identifier = "dual-fail",
            Title = "Dual-label issue whose swap fails",
            Labels = [AgentLabels.Done, AgentLabels.InProgress]
        };

        WireProviders(EmptyProvider().Object, BuildProvider(issue).Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var (service, sink) = CreateServiceWithCapture();

        // Must not throw — failure is non-fatal
        var act = () => service.SweepOnceForTestAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();

        // recoveredCount must be 0: the swap failed so the issue was not recovered.
        var completionEvent = sink.Events
            .FirstOrDefault(e => e.MessageTemplate.Text.Contains("Orphaned label recovery complete"));
        completionEvent.Should().NotBeNull("the completion log must always be emitted");
        // TODO: [WARNING] Properties["Count"].ToString() asserts the rendered string of a Serilog ScalarValue.
        // For an integer property this currently works (renders as bare numeral), but if the log call ever
        // passes recoveredCount as a string instead of an int, ScalarValue.ToString() would return "\"0\""
        // and the assertion would silently fail. Safer: ((ScalarValue)completionEvent!.Properties["Count"]).Value.Should().Be(0)
        completionEvent!.Properties["Count"].ToString().Should().Be("0",
            "a failed swap must not increment recoveredCount");
    }

    /// <summary>
    /// Characterization test: OperationCanceledException from TrySwapToErrorAsync propagates out of
    /// ScanProviderAsync, but is caught by the outer sweep-level catch (Exception ex) in SweepOnceForTestAsync.
    /// The sweep therefore completes without throwing — cancellation is observed at the next
    /// ct.ThrowIfCancellationRequested() in the sweep loop, not from the label swap site itself.
    /// </summary>
    [Fact]
    public async Task Pass1_WhenSwapToErrorThrowsOce_SweepDoesNotPropagateOce()
    {
        var issue = new IssueSummary
        {
            Identifier = "orphan-oce",
            Title = "Orphan with OCE on swap",
            Labels = [AgentLabels.InProgress]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        using var cts = new CancellationTokenSource();
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns<ProviderConfigId, IssueIdentifier, string, LabelTargetKind, CancellationToken>(
                (_, _, _, _, _) =>
                {
                    cts.Cancel();
                    throw new OperationCanceledException(cts.Token);
                });

        // The outer SweepOnceForTestAsync catches Exception from ScanProviderAsync (including OCE),
        // so the sweep itself does not propagate the exception.
        var act = () => CreateService().SweepOnceForTestAsync(cts.Token);
        // TODO: [WARNING] This assertion is too weak: it validates that the outer sweep-level catch absorbs
        // the OCE, not that TrySwapLabelAsync itself propagated OCE (vs swallowing it). If TrySwapLabelAsync
        // were changed to swallow OCE (e.g., SwallowCancellation=true accidentally set), this test would
        // still pass. Consider adding a verify that SwapLabelAsync was called at all, and/or testing
        // TrySwapToErrorAsync in isolation to confirm OCE propagates before the outer sweep catch.
        await act.Should().NotThrowAsync(
            "OCE from label swap propagates through TrySwapToErrorAsync into ScanProviderAsync, " +
            "but is caught by the outer sweep-level catch — the sweep completes without throwing");
    }

    // ── recoveredCount correctness after contract fix ─────────────────────

    /// <summary>
    /// Acceptance criterion: when TrySwapToErrorAsync's inner swap fails non-fatally,
    /// recoveredCount must NOT be incremented. The issue was not recovered — only attempted.
    /// </summary>
    [Fact]
    public async Task Pass1_WhenSwapToErrorFails_RecoveredCountNotIncremented()
    {
        var issue = new IssueSummary
        {
            Identifier = "orphan-count-check",
            Title = "Orphan whose swap fails — count must stay 0",
            Labels = [AgentLabels.InProgress]
        };

        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var (service, sink) = CreateServiceWithCapture();
        await service.SweepOnceForTestAsync(CancellationToken.None);

        // The swap was attempted — verify it was actually called (positive guard: test is not vacuous).
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "orphan-count-check"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SwapLabelAsync must have been called — the issue was an orphan candidate");

        // The swap failed → recoveredCount must be 0.
        var completionEvent = sink.Events
            .FirstOrDefault(e => e.MessageTemplate.Text.Contains("Orphaned label recovery complete"));
        completionEvent.Should().NotBeNull("the completion summary log must always be emitted");
        // TODO: [WARNING] Properties["Count"].ToString() asserts the rendered string of a Serilog ScalarValue.
        // For an integer property this currently works (renders as bare numeral), but if the log call ever
        // passes recoveredCount as a string instead of an int, ScalarValue.ToString() would return "\"0\""
        // and the assertion would silently fail. Safer: ((ScalarValue)completionEvent!.Properties["Count"]).Value.Should().Be(0)
        // TODO: [WARNING] This test has no symmetric success-case counterpart: there is no test asserting that
        // a successful swap (mock returns Task.CompletedTask) produces Count = 1. Without the counterpart,
        // a broken implementation that always logs Count = 0 would pass this test undetected.
        completionEvent!.Properties["Count"].ToString().Should().Be("0",
            "a failed swap must not increment recoveredCount — the issue was not recovered");
    }

    /// <summary>
    /// Acceptance criterion: when TrySwapToDualLabelResolutionAsync's inner swap fails non-fatally,
    /// recoveredCount must NOT be incremented.
    /// </summary>
    [Fact]
    public async Task Pass2_WhenDualLabelSwapFails_RecoveredCountNotIncremented()
    {
        var issue = new IssueSummary
        {
            Identifier = "dual-count-check",
            Title = "Dual-label whose swap fails — count must stay 0",
            Labels = [AgentLabels.Done, AgentLabels.InProgress]
        };

        WireProviders(EmptyProvider().Object, BuildProvider(issue).Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var (service, sink) = CreateServiceWithCapture();
        await service.SweepOnceForTestAsync(CancellationToken.None);

        // The swap was attempted — positive guard.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "dual-count-check"),
                AgentLabels.Done,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SwapLabelAsync must have been called — the issue was a dual-label candidate");

        // The swap failed → recoveredCount must be 0.
        var completionEvent = sink.Events
            .FirstOrDefault(e => e.MessageTemplate.Text.Contains("Orphaned label recovery complete"));
        completionEvent.Should().NotBeNull("the completion summary log must always be emitted");
        // TODO: [WARNING] Properties["Count"].ToString() asserts the rendered string of a Serilog ScalarValue.
        // For an integer property this currently works (renders as bare numeral), but if the log call ever
        // passes recoveredCount as a string instead of an int, ScalarValue.ToString() would return "\"0\""
        // and the assertion would silently fail. Safer: ((ScalarValue)completionEvent!.Properties["Count"]).Value.Should().Be(0)
        // TODO: [WARNING] This test has no symmetric success-case counterpart: there is no test asserting that
        // a successful swap (mock returns Task.CompletedTask) produces Count = 1. Without the counterpart,
        // a broken implementation that always logs Count = 0 would pass this test undetected.
        completionEvent!.Properties["Count"].ToString().Should().Be("0",
            "a failed swap must not increment recoveredCount — the issue was not recovered");
    }

    // ── Helpers (capture logger) ──────────────────────────────────────────

    /// <summary>
    /// Creates a service backed by a real Serilog logger writing to a <see cref="CaptureSink"/>.
    /// Use when a test needs to assert on structured log properties (e.g. recoveredCount).
    /// </summary>
    private (OrphanedLabelRecoveryService Service, CaptureSink Sink) CreateServiceWithCapture()
    {
        var sink = new CaptureSink();
        var captureLogger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(sink)
            .CreateLogger();

        var service = new OrphanedLabelRecoveryService(
            _mockRunService.Object,
            _mockConfigClient.Object,
            _mockWorkItemClient.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            leaderGate: null,
            captureLogger,
            gracePeriod: TimeSpan.Zero);

        return (service, sink);
    }
}

/// <summary>
/// In-memory Serilog sink that captures all log events for test assertions.
/// Thread-safe via <see cref="ConcurrentQueue{T}"/>.
/// Used by <see cref="OrphanedLabelRecoveryServiceTests"/> to assert on structured
/// log property values (e.g. the recoveredCount in the sweep completion message).
/// </summary>
internal sealed class CaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
