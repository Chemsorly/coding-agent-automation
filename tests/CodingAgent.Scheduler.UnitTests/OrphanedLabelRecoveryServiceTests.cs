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
/// Each sweep invocation creates four <see cref="IIssueProvider"/> instances:
/// <list type="bullet">
///   <item>Pass 1: queries agent:in-progress issues.</item>
///   <item>Pass 2: queries agent:done issues.</item>
///   <item>Pass 3: queries agent:error issues.</item>
///   <item>Pass 4: queries agent:needs-refinement issues.</item>
/// </list>
/// Helper methods name the provider instances explicitly to prevent cross-contamination.
///
/// Covers the new dual-label sweep introduced for issue #2648:
/// <list type="bullet">
///   <item>Pass 1 (agent:in-progress scan) — dual-label route via TryRecoverSingleIssueAsync.</item>
///   <item>Pass 2 (agent:done scan) — ScanProviderForDualLabelIssuesAsync.</item>
///   <item>Pass 3 (agent:error scan) — ScanProviderForDualLabelIssuesAsync.</item>
///   <item>Pass 4 (agent:needs-refinement scan) — ScanProviderForDualLabelIssuesAsync.</item>
///   <item>Precedence resolution, idempotency, Defense 3 guard, agent:generated coexistence.</item>
/// </list>
/// </summary>
[Collection("Metrics")]
public sealed class OrphanedLabelRecoveryServiceTests : IDisposable
{
    private readonly Mock<IOrchestratorRunService> _mockRunService = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItemClient = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new(MockBehavior.Strict);
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private static readonly string[] InProgressLabels = ["agent:in-progress"];
    private readonly CancellationTokenSource _cts = new();

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

        // Default: no project has an epic tracker.
        _mockConfigClient
            .Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());

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
    /// Wires the provider factory so that each call to CreateIssueProvider returns the provider
    /// for the corresponding sweep pass:
    /// <list type="bullet">
    ///   <item>Call 1 → <paramref name="pass1Provider"/> (agent:in-progress scan).</item>
    ///   <item>Call 2 → <paramref name="pass2Provider"/> (agent:done scan).</item>
    ///   <item>Call 3 → <paramref name="pass3Provider"/> (agent:error scan; defaults to empty).</item>
    ///   <item>Call 4 → <paramref name="pass4Provider"/> (agent:needs-refinement scan; defaults to empty).</item>
    /// </list>
    /// Passing <c>null</c> for <paramref name="pass3Provider"/> or <paramref name="pass4Provider"/>
    /// defaults to an empty provider, keeping all existing two-argument call sites unchanged.
    /// </summary>
    // TODO: The providers array has a fixed length of 4 (one slot per sweep pass). If a fifth pass
    // is added without updating this helper, the Returns lambda will throw IndexOutOfRangeException
    // at test runtime rather than producing a meaningful assertion failure. When adding a new pass,
    // update the providers array and add the corresponding parameter here.
    // TODO: callCount++ is a non-atomic read-modify-write. The [Collection("SchedulerTiming")]
    // attribute prevents test-level parallelism today, so no data race is reachable. However, the
    // previous implementation used Interlocked.Increment which was explicitly thread-safe. If this
    // helper is ever used in a context with concurrent CreateIssueProvider calls, replace callCount++
    // with Interlocked.Increment(ref callCount) - 1 to restore safety.
    private void WireProviders(
        IIssueProvider pass1Provider,
        IIssueProvider pass2Provider,
        IIssueProvider? pass3Provider = null,
        IIssueProvider? pass4Provider = null)
    {
        var providers = new[]
        {
            pass1Provider,
            pass2Provider,
            pass3Provider ?? EmptyProvider().Object,
            pass4Provider ?? EmptyProvider().Object
        };
        var callCount = 0;
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(() => providers[callCount++]);
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

    private OrphanedLabelRecoveryService CreateServiceWithGate(ILeaderGate? leaderGate) => new(
        _mockRunService.Object,
        _mockConfigClient.Object,
        _mockWorkItemClient.Object,
        _mockProviderFactory.Object,
        _mockLabelService.Object,
        leaderGate,
        _mockLogger.Object,
        TimeSpan.FromMilliseconds(50)); // Short grace period for fast tests

    /// <summary>
    /// Defaults the hosted-service sweep-loop tests rely on: a pipeline config with the sweep interval,
    /// and no templates unless the test configures one.
    /// </summary>
    private void UseHostedServiceDefaults()
    {
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 30 });
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
    }

    private void SetupTemplateWithProvider(string providerId)
    {
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "Test Template", IssueProviderId = providerId, RepoProviderId = "repo-1" }
            });
    }

    private void SetupProviderConfig(string providerId)
    {
        _mockConfigClient
            .Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = providerId,
                    Kind = ProviderKind.Issue,
                    DisplayName = $"Provider {providerId}",
                    ProviderType = "GitHub",
                    Settings = new Dictionary<string, string>()
                }
            });
    }

    private void SetupIssueProvider(string providerId, params IssueSummary[] issues)
    {
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = issues,
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        // Default GetIssueAsync: return issue still with agent:in-progress (genuinely orphaned).
        // Individual tests can override this via SetupIssueProviderWithGetIssue for specific scenarios.
        foreach (var issue in issues)
        {
            mockIssueProvider
                .Setup(p => p.GetIssueAsync(issue.Identifier, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new IssueDetail
                {
                    Identifier = issue.Identifier,
                    Title = issue.Title,
                    Description = "",
                    Labels = issue.Labels
                });
        }

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == providerId)))
            .Returns(mockIssueProvider.Object);
    }
    public void Dispose()
    {
        _cts.Dispose();
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

    // ── Trackers swept: template trackers plus enabled projects' epic trackers (#3158) ──

    private void SetupTrackers(IReadOnlyList<PipelineJobTemplate> templates, params PipelineProject[] projects)
    {
        _mockConfigClient
            .Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        _mockConfigClient
            .Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(projects);
    }

    [Fact]
    public async Task EpicTrackerOfAnEnabledProject_IsSwept()
    {
        // provider-1 is only a project's epic tracker: a project epic stuck in agent:in-progress there is recovered
        SetupTrackers([], new PipelineProject { Id = "p1", Name = "P1", Enabled = true, EpicIssueProviderId = "provider-1" });
        var epic = new IssueSummary { Identifier = "7", Title = "Stuck epic", Labels = [AgentLabels.InProgress] };
        WireProviders(BuildProvider(epic).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.Is<ProviderConfigId>(p => p.Value == "provider-1"),
                It.Is<IssueIdentifier>(i => i.Value == "7"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task EpicTrackerOfADisabledProject_IsNotSwept()
    {
        SetupTrackers([], new PipelineProject { Id = "p1", Name = "P1", Enabled = false, EpicIssueProviderId = "provider-1" });
        WireProviders(EmptyProvider().Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockProviderFactory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task EpicTrackerThatIsAlsoATemplatesTracker_IsSweptOnce()
    {
        SetupTrackers(
            [new PipelineJobTemplate { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "repo-1" }],
            new PipelineProject { Id = "p1", Name = "P1", Enabled = true, EpicIssueProviderId = "provider-1" });
        WireProviders(EmptyProvider().Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // One tracker, four passes
        _mockProviderFactory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Exactly(4));
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

    // ── Pass 3 (agent:error scan) ──────────────────────────────────────────

    /// <summary>
    /// Acceptance criterion: an issue bearing {agent:error, agent:next} is detected via Pass 3
    /// (the new agent:error query) and resolved to agent:error (higher precedence wins).
    /// This is the primary production scenario described in issue #3039.
    /// </summary>
    [Fact]
    public async Task Pass3_WhenErrorIssueHasDualLabelWithNext_ResolvesToError()
    {
        var issue = new IssueSummary
        {
            Identifier = "301",
            Title = "Error + next dual-label issue",
            Labels = [AgentLabels.Error, AgentLabels.Next]
        };

        // Pass 1 (in-progress query): empty — no in-progress label.
        // Pass 2 (done query): empty — no done label.
        // Pass 3 (error query): returns the dual-label issue.
        // Pass 4 (needs-refinement query): empty.
        WireProviders(EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // agent:error (index 1) beats agent:next (index 9) in DualLabelResolutionPrecedence → keep error.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "301"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "dual-label {error, next} issue detected via Pass 3 must be resolved to agent:error");

        // Verify agent:next was never chosen as the winner.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "301"),
                AgentLabels.Next,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "agent:next must not be selected as the winner when agent:error is present");
    }

    /// <summary>
    /// An issue with only agent:error (single label) must not be touched by Pass 3.
    /// </summary>
    [Fact]
    public async Task Pass3_SingleLabelError_NotTouched()
    {
        var issue = new IssueSummary
        {
            Identifier = "302",
            Title = "Single-label error issue",
            Labels = [AgentLabels.Error]
        };

        WireProviders(EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "single-label agent:error issue must not be modified by Pass 3");
    }

    /// <summary>
    /// Defense 3: a dual-label {agent:error, agent:next} issue with an active WorkItem
    /// must not be touched by Pass 3.
    /// </summary>
    [Fact]
    public async Task Pass3_WhenActiveWorkItem_DualLabelErrorNextSkipped()
    {
        var issue = new IssueSummary
        {
            Identifier = "303",
            Title = "Error + next with live agent",
            Labels = [AgentLabels.Error, AgentLabels.Next]
        };

        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync(
                It.Is<string>(id => id == "303"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        WireProviders(EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "dual-label {error, next} issue with an active WorkItem must not be modified (Defense 3)");

        // Positive guard: verify the distributed check fired.
        _mockWorkItemClient.Verify(
            w => w.IsIssueDistributedAsync("303", "provider-1", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "IsIssueDistributedAsync must have been called to reach the Times.Never assertion above");
    }

    /// <summary>
    /// Per-issue failure isolation: a swap failure in Pass 3 must not abort the sweep.
    /// </summary>
    // TODO: This test only asserts that no exception propagates. It does not verify that the sweep
    // continued past the failing issue (e.g. that Pass 4 still ran or that a second issue in Pass 3
    // would have been attempted). A broken implementation that exits the foreach after catching the
    // swap failure — rather than continuing — would still pass this test. Consider adding a second
    // issue to Pass 3 or asserting that Pass 4 ran, mirroring the secondary assertion in
    // Pass2_WhenDualLabelSwapFails_SweepContinues (which also checks recoveredCount via the log).
    [Fact]
    public async Task Pass3_WhenSwapFails_SweepContinues()
    {
        var issue = new IssueSummary
        {
            Identifier = "304",
            Title = "Error + next whose swap fails",
            Labels = [AgentLabels.Error, AgentLabels.Next]
        };

        WireProviders(EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object, EmptyProvider().Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var act = () => CreateService().SweepOnceForTestAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("swap failure in Pass 3 must be non-fatal — sweep must continue");
    }

    /// <summary>
    /// Cross-pass deduplication: an issue with {agent:in-progress, agent:error} appears in both
    /// Pass 1 (agent:in-progress query) and Pass 3 (agent:error query). Pass 1 resolves it and
    /// records it in pass1ResolvedIdentifiers; Pass 3 must skip it. SwapLabelAsync must be called
    /// exactly once across the entire sweep.
    /// </summary>
    // TODO: This test only covers the Pass 1 → Pass 3 overlap. There is no test for the Pass 2 → Pass 3
    // overlap ({agent:done, agent:error} appearing in both Pass 2 and Pass 3) or for Pass 2 → Pass 4,
    // Pass 3 → Pass 4 overlaps. Since Passes 2–4 never write to pass1ResolvedIdentifiers, a dual-label
    // issue that spans any two of those passes is processed twice in the same sweep (double-swap).
    // Add deduplication tests for these overlaps once the underlying fix (passes 2–4 writing to the
    // shared set) is implemented. See the TODO on pass1ResolvedIdentifiers in RecoverOrphanedLabelsAsync.
    [Fact]
    public async Task WhenInProgressErrorIssueAppearsInBothPass1AndPass3_SwapCalledOnlyOnce()
    {
        var issue = new IssueSummary
        {
            Identifier = "305",
            Title = "In-progress + error dual-label in both Pass 1 and Pass 3",
            Labels = [AgentLabels.InProgress, AgentLabels.Error]
        };

        // Pass 1 (in-progress query): returns the issue — Pass 1 resolves it first.
        // Pass 2 (done query): empty.
        // Pass 3 (error query): returns the same issue — must be skipped via pass1ResolvedIdentifiers.
        // Pass 4 (needs-refinement query): empty.
        WireProviders(BuildProvider(issue).Object, EmptyProvider().Object, BuildProvider(issue).Object, EmptyProvider().Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // pass1ResolvedIdentifiers guard must prevent Pass 3 from re-processing the same issue.
        // SwapLabelAsync must be called exactly once across both passes.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "305"),
                AgentLabels.Error,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "SwapLabelAsync must be called exactly once — Pass 3 must skip the issue already resolved by Pass 1");
    }

    // ── Pass 4 (agent:needs-refinement scan) ───────────────────────────────

    // TODO: There is no test covering the Pass 1 + Pass 4 cross-pass deduplication case.
    // WhenInProgressErrorIssueAppearsInBothPass1AndPass3_SwapCalledOnlyOnce (above) covers
    // Pass 1 + Pass 3 ({agent:in-progress, agent:error}). The pass1ResolvedIdentifiers set is
    // shared with Pass 4 identically, so a regression that breaks the guard specifically for the
    // needs-refinement path would go undetected. Add a test for {agent:in-progress, agent:needs-refinement}
    // appearing in both Pass 1 and Pass 4, asserting SwapLabelAsync is called exactly once.

    /// <summary>
    /// Acceptance criterion: an issue bearing {agent:needs-refinement, agent:next} is detected via
    /// Pass 4 (the new agent:needs-refinement query) and resolved to agent:needs-refinement.
    /// This is the second primary scenario described in issue #3039.
    /// </summary>
    [Fact]
    public async Task Pass4_WhenNeedsRefinementIssueHasDualLabelWithNext_ResolvesNeedsRefinement()
    {
        var issue = new IssueSummary
        {
            Identifier = "401",
            Title = "Needs-refinement + next dual-label issue",
            Labels = [AgentLabels.NeedsRefinement, AgentLabels.Next]
        };

        // All passes empty except Pass 4.
        WireProviders(EmptyProvider().Object, EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        // agent:needs-refinement (index 2) beats agent:next (index 9) → keep needs-refinement.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "401"),
                AgentLabels.NeedsRefinement,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Once,
            "dual-label {needs-refinement, next} issue detected via Pass 4 must be resolved to agent:needs-refinement");

        // Verify agent:next was never chosen as the winner.
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.Is<IssueIdentifier>(i => i.Value == "401"),
                AgentLabels.Next,
                LabelTargetKind.Issue,
                It.IsAny<CancellationToken>()),
            Times.Never,
            "agent:next must not be selected as the winner when agent:needs-refinement is present");
    }

    /// <summary>
    /// Defense 3: a dual-label {agent:needs-refinement, agent:next} issue with an active WorkItem
    /// must not be touched by Pass 4.
    /// </summary>
    [Fact]
    public async Task Pass4_WhenActiveWorkItem_DualLabelNeedsRefinementNextSkipped()
    {
        var issue = new IssueSummary
        {
            Identifier = "402",
            Title = "Needs-refinement + next with live agent",
            Labels = [AgentLabels.NeedsRefinement, AgentLabels.Next]
        };

        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync(
                It.Is<string>(id => id == "402"),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        WireProviders(EmptyProvider().Object, EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object);

        await CreateService().SweepOnceForTestAsync(CancellationToken.None);

        _mockLabelService.Verify(
            l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(),
                It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(),
                It.IsAny<CancellationToken>()),
            Times.Never,
            "dual-label {needs-refinement, next} issue with an active WorkItem must not be modified (Defense 3)");

        // Positive guard: verify the distributed check fired.
        _mockWorkItemClient.Verify(
            w => w.IsIssueDistributedAsync("402", "provider-1", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce,
            "IsIssueDistributedAsync must have been called to reach the Times.Never assertion above");
    }

    /// <summary>
    /// Per-issue failure isolation: a swap failure in Pass 4 must not abort the sweep.
    /// </summary>
    // TODO: This test only asserts that no exception propagates. It does not verify that the sweep
    // continued past the failing issue or that subsequent operations executed. A broken implementation
    // that exits the foreach after catching the swap failure — rather than continuing — would still
    // pass this test. Consider adding a second issue to Pass 4 or verifying continuation via a log
    // assertion, mirroring the secondary assertion in Pass2_WhenDualLabelSwapFails_SweepContinues.
    [Fact]
    public async Task Pass4_WhenSwapFails_SweepContinues()
    {
        var issue = new IssueSummary
        {
            Identifier = "403",
            Title = "Needs-refinement + next whose swap fails",
            Labels = [AgentLabels.NeedsRefinement, AgentLabels.Next]
        };

        WireProviders(EmptyProvider().Object, EmptyProvider().Object, EmptyProvider().Object, BuildProvider(issue).Object);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Provider down"));

        var act = () => CreateService().SweepOnceForTestAsync(CancellationToken.None);
        await act.Should().NotThrowAsync("swap failure in Pass 4 must be non-fatal — sweep must continue");
    }

    // ── Hosted service sweep loop ───────────────────────────────────

    [Fact]
    public async Task Sweep_SwapsOrphanedIssues()
    {
        UseHostedServiceDefaults();

        // Arrange: one template with one provider, one orphaned issue
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Orphaned issue",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("42", "provider-1"))
            .Returns(false);

        var labelSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => labelSwapCalled.TrySetResult());

        // Act: start the service (grace period is 60s, but we'll cancel after the swap)
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(labelSwapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        // Assert: the swap was called
        completed.Should().BeSameAs(labelSwapCalled.Task, "SwapLabelAsync should have been called after grace period");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_SkipsActiveRuns()
    {
        UseHostedServiceDefaults();

        // Arrange: one template, one issue that IS being processed
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Active issue",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("42", "provider-1"))
            .Returns(true);

        // Gate on the IsIssueBeingProcessed call — fires when the sweep has reached this check,
        // meaning it is deterministically past any point where a swap could be triggered.
        var sweepReachedCheck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("42", "provider-1"))
            .Returns(true)
            .Callback(() => sweepReachedCheck.TrySetResult());

        // Act: start service, wait for the sweep to reach the IsIssueBeingProcessed check
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(sweepReachedCheck.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(sweepReachedCheck.Task, "sweep must reach the IsIssueBeingProcessed check");

        // Brief yield so any async continuations after the check complete
        await Task.Yield();

        // Assert: SwapLabelAsync was NOT called — active run was skipped
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_ContinuesOnLabelSwapFailure()
    {
        UseHostedServiceDefaults();

        // Arrange: two orphaned issues, first swap throws, second should still succeed
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1",
            new IssueSummary { Identifier = "1", Title = "Issue 1", Labels = new[] { "agent:in-progress" } },
            new IssueSummary { Identifier = "2", Title = "Issue 2", Labels = new[] { "agent:in-progress" } });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "1", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("GitHub API error"));

        var secondSwapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "2", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => secondSwapCalled.TrySetResult());

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(secondSwapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        // Assert: second issue was still processed despite first failure
        completed.Should().BeSameAs(secondSwapCalled.Task, "Second swap should succeed despite first failure");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_ContinuesOnProviderScanFailure()
    {
        UseHostedServiceDefaults();

        // Arrange: two providers, first throws at provider factory, second should still be scanned
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "Template 1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "Template 2", IssueProviderId = "provider-2", RepoProviderId = "r2" }
        };
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        // Both providers configured
        _mockConfigClient
            .Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "provider-1", Kind = ProviderKind.Issue, DisplayName = "Provider 1", ProviderType = "GitHub", Settings = new Dictionary<string, string>() },
                new() { Id = "provider-2", Kind = ProviderKind.Issue, DisplayName = "Provider 2", ProviderType = "GitHub", Settings = new Dictionary<string, string>() }
            });

        // Provider-1 factory throws (simulates scan failure)
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "provider-1")))
            .Throws(new InvalidOperationException("Provider unavailable"));

        // Provider-2 succeeds
        SetupIssueProvider("provider-2", new IssueSummary
        {
            Identifier = "99",
            Title = "Orphaned",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("99", "provider-2"))
            .Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-2", "99", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        // Assert: second provider was scanned despite first failure
        completed.Should().BeSameAs(swapCalled.Task, "Second provider should be scanned despite first provider failure");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task NoTemplates_SkipsSweep()
    {
        UseHostedServiceDefaults();

        // Arrange: no templates configured (default mock returns empty)
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        // Gate: GetAllTemplatesAsync is always called at the start of each sweep.
        // When it returns (empty), the sweep is complete — no providers will be scanned.
        var sweepCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>())
            .Callback(() => sweepCompleted.TrySetResult());

        // Act: start and wait for the sweep to complete
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(sweepCompleted.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(sweepCompleted.Task, "sweep must complete after grace period");

        // Brief yield so any async continuations after GetAllTemplatesAsync complete
        await Task.Yield();

        // Assert: no provider scans attempted
        _mockConfigClient.Verify(
            s => s.GetProviderConfigsWithSecretsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task MultipleProviders_ScansAll()
    {
        UseHostedServiceDefaults();

        // Arrange: two templates with different providers
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "T2", IssueProviderId = "provider-2", RepoProviderId = "r2" }
        };
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        // Both providers in one GetProviderConfigsWithSecretsAsync call (method returns full list).
        // The recovery service calls the provider APIs directly, so it needs live tokens — the
        // masked GetProviderConfigsAsync form would hand it "****" instead of a usable credential.
        _mockConfigClient
            .Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "provider-1", Kind = ProviderKind.Issue, DisplayName = "Provider 1", ProviderType = "GitHub", Settings = new Dictionary<string, string>() },
                new() { Id = "provider-2", Kind = ProviderKind.Issue, DisplayName = "Provider 2", ProviderType = "GitHub", Settings = new Dictionary<string, string>() }
            });
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "10",
            Title = "Issue A",
            Labels = InProgressLabels
        });
        SetupIssueProvider("provider-2", new IssueSummary
        {
            Identifier = "20",
            Title = "Issue B",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>()))
            .Returns(false);

        var swapCount = 0;
        var allSwapsDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() =>
            {
                if (Interlocked.Increment(ref swapCount) >= 2)
                    allSwapsDone.TrySetResult();
            });

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(allSwapsDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: both providers were scanned
        completed.Should().BeSameAs(allSwapsDone.Task, "Both providers should be scanned");
        _mockLabelService.Verify(
            l => l.SwapLabelAsync("provider-1", "10", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);
        _mockLabelService.Verify(
            l => l.SwapLabelAsync("provider-2", "20", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()),
            Times.Once);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ConfigIntervalBelowMinimum_ClampedWithWarning()
    {
        UseHostedServiceDefaults();

        // Arrange: config with interval below minimum (1 minute < 5 minute minimum)
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 1 });

        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1"); // no issues

        // Track sweep calls to verify the service starts with a valid interval
        var sweepCount = 0;
        var firstSweepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" }
            })
            .Callback(() =>
            {
                if (Interlocked.Increment(ref sweepCount) == 1)
                    firstSweepDone.TrySetResult();
            });

        // Act: start service — it should not throw despite bad config value
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(firstSweepDone.Task, Task.Delay(TimeSpan.FromSeconds(90)));

        // Assert: service started successfully (no ArgumentOutOfRangeException from PeriodicTimer)
        completed.Should().BeSameAs(firstSweepDone.Task, "Service should start and perform first sweep despite low interval config");
        // TODO: Assert that a warning was logged containing the clamping message (e.g., verify
        // _mockLogger received a Warning call mentioning "below minimum" and the clamped value).

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Cancellation_StopsGracefully()
    {
        UseHostedServiceDefaults();

        // Arrange: empty templates
        using var service = CreateServiceWithGate(null);

        // Act: start and immediately cancel
        await service.StartAsync(_cts.Token);
        _cts.Cancel();

        // Assert: stop completes without exception
        var stopTask = service.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(30));
        stopTask.IsCompletedSuccessfully.Should().BeTrue();
    }

    [Fact]
    public async Task DeduplicatesProviderIds()
    {
        UseHostedServiceDefaults();

        // Arrange: two templates with the SAME provider ID
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" },
            new() { Id = "t2", Name = "T2", IssueProviderId = "provider-1", RepoProviderId = "r2" }
        };
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "5",
            Title = "Issue",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("5", "provider-1"))
            .Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "5", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Track when the full sweep (all four passes) has completed by counting
        // GetProviderConfigsWithSecretsAsync calls. Passes 1, 2, 3, and 4 each call it once,
        // so the 4th call signals that the entire sweep is done.
        var allPassesCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var providerConfigCallCount = 0;
        _mockConfigClient
            .Setup(s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "provider-1",
                    Kind = ProviderKind.Issue,
                    DisplayName = "Provider provider-1",
                    ProviderType = "GitHub",
                    Settings = new Dictionary<string, string>()
                }
            })
            .Callback(() =>
            {
                if (Interlocked.Increment(ref providerConfigCallCount) >= 4)
                    allPassesCompleted.TrySetResult();
            });

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().BeSameAs(swapCalled.Task,
            "SwapLabelAsync should have been called — if this timed out, the sweep either " +
            "never ran or the issue was incorrectly skipped by one of the defense checks");

        // Wait for all passes to finish before asserting the call count. Pass 1 fires SwapLabelAsync
        // (the signal above), but Passes 2, 3, and 4 run afterward. Asserting immediately after Pass 1 is a
        // race — the remaining GetProviderConfigsWithSecretsAsync calls may not have occurred yet.
        var allCompleted = await Task.WhenAny(allPassesCompleted.Task, Task.Delay(TimeSpan.FromSeconds(30)));
        allCompleted.Should().BeSameAs(allPassesCompleted.Task,
            "All four sweep passes should have completed within the timeout");

        // Assert: provider config was loaded four times (once per pass: Pass 1, 2, 3, 4) but NOT
        // eight times (which would happen without deduplication of the two templates sharing provider-1).
        // This assertion is tied to the current implementation detail of exactly 4 scan passes.
        // If passes are added or removed, update this count to numberOfPasses * 1 (deduplication
        // ensures each provider is scanned once per pass regardless of how many templates share it).
        _mockConfigClient.Verify(
            s => s.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()),
            Times.Exactly(4));

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task PeriodicSweep_ServiceRemainsRunningAfterFirstSweep()
    {
        UseHostedServiceDefaults();

        // Arrange: set up a valid config and provider so the first sweep completes successfully
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1"); // no issues

        var sweepCount = 0;
        _mockConfigClient
            .Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t1", Name = "T1", IssueProviderId = "provider-1", RepoProviderId = "r1" }
            })
            .Callback(() => Interlocked.Increment(ref sweepCount));

        // Gate on the interval load, not on the sweep start: the service loads the config only after
        // the first sweep has completed, so every assertion below is already settled when this fires.
        var intervalLoaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 30 })
            .Callback(() => intervalLoaded.TrySetResult());

        // Act: start the service and wait for the first sweep to complete
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(intervalLoaded.Task, Task.Delay(TimeSpan.FromSeconds(90)));
        completed.Should().BeSameAs(intervalLoaded.Task,
            "First sweep should complete after grace period and the periodic interval should be loaded");

        // Assert: after the first sweep, the service is still running (entered periodic loop).
        // The old single-run implementation would have ExecuteTask completed here.
        // With periodic behavior, ExecuteTask remains incomplete until cancellation.
        // The short settle only gives a loop that wrongly exits after loading the config time to
        // complete ExecuteTask; nothing asserted here depends on it.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        service.ExecuteTask!.IsCompleted.Should().BeFalse(
            "Service should remain running in the periodic loop after first sweep — " +
            "if it completed, the periodic timer was never entered");

        // Verify exactly one sweep ran (the initial sweep), proving the service is now
        // waiting for the next timer tick rather than exiting or looping without a timer.
        sweepCount.Should().Be(1,
            "Only the initial sweep should have run — the service should be blocked on " +
            "PeriodicTimer.WaitForNextTickAsync, not completing or spinning");

        // Verify config was loaded exactly once to establish the timer interval
        _mockConfigClient.Verify(
            c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()),
            Times.Once,
            "Config should be loaded exactly once after first sweep to determine periodic interval");

        // Verify cancellation causes the service to exit cleanly from the timer loop
        _cts.Cancel();
        var stopTask = service.StopAsync(CancellationToken.None);
        await stopTask.WaitAsync(TimeSpan.FromSeconds(30));
        stopTask.IsCompletedSuccessfully.Should().BeTrue(
            "Service should stop gracefully when cancelled while waiting for timer tick");
    }

    // TODO: Inject TimeProvider (available in .NET 8+) into OrphanedLabelRecoveryService to enable
    // testing actual periodic sweep execution without real wall-clock delays. With FakeTimeProvider,
    // tests could advance time and verify that multiple sweeps occur at the configured interval.
    // Currently, the 5-minute minimum interval makes it impractical to test multiple ticks in a unit test.

    [Fact]
    public async Task Sweep_SkipsRecentlyCompletedIssue_RaceConditionReproduction()
    {
        UseHostedServiceDefaults();

        // Reproduces the race: run completes → removal from active tracking → sweep fires
        // → should NOT swap to agent:error (acceptance criteria #3)
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "1635",
            Title = "Issue with race",
            Labels = InProgressLabels
        });

        // Run was already removed from active tracking (simulates the race window)
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("1635", "provider-1"))
            .Returns(false);

        // Gate: WasRecentlyCompleted is called during the sweep — fires when sweep has evaluated this issue
        var sweepCheckedRecent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("1635", "provider-1"))
            .Returns(true)
            .Callback(() => sweepCheckedRecent.TrySetResult());

        // Act: start the service and wait for sweep to reach the recent-completion check
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(sweepCheckedRecent.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(sweepCheckedRecent.Task, "sweep must reach the WasRecentlyCompleted check");

        // Brief yield so async continuations after the check complete
        await Task.Yield();

        // Assert: SwapLabelAsync was NOT called — grace period protected the issue
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_SkipsIssueWithActiveWorkItem_Defense3()
    {
        UseHostedServiceDefaults();

        // Reproduces the false-positive: agent:in-progress issue has a live WorkItem in Postgres
        // (agent connected and running), but SchedulerRunQueryService.IsIssueBeingProcessed always
        // returns false. Without Defense 3, the recovery service would swap to agent:error.
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "2087",
            Title = "Active issue with live agent",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("2087", "provider-1"))
            .Returns(false); // always false in Scheduler

        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("2087", "provider-1"))
            .Returns(false);

        // Gate: IsIssueDistributedAsync is the Defense 3 check — fires when sweep evaluated this issue
        var defense3Checked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync("2087", "provider-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true)
            .Callback(() => defense3Checked.TrySetResult());

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(defense3Checked.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(defense3Checked.Task, "sweep must reach the IsIssueDistributedAsync check");

        // Brief yield so async continuations after the check complete
        await Task.Yield();

        // Assert: SwapLabelAsync was NOT called — live WorkItem blocked the false-positive
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Defense 3 must prevent swapping to agent:error when a live WorkItem exists");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_WhenDefense3ApiFails_SkipsIssueFailSafe()
    {
        UseHostedServiceDefaults();

        // If IsIssueDistributedAsync throws (API unreachable), the service must NOT swap to
        // agent:error — it should skip the issue to avoid false-positives.
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "2087",
            Title = "Issue — API check unavailable",
            Labels = InProgressLabels
        });

        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("2087", "provider-1"))
            .Returns(false);

        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("2087", "provider-1"))
            .Returns(false);

        // Gate: Defense 3 throws — fires when sweep has attempted the API check
        var defense3Attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockWorkItemClient
            .Setup(w => w.IsIssueDistributedAsync("2087", "provider-1", It.IsAny<CancellationToken>()))
            .Callback(() => defense3Attempted.TrySetResult())
            .ThrowsAsync(new HttpRequestException("API unreachable"));

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(defense3Attempted.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(defense3Attempted.Task, "sweep must attempt the IsIssueDistributedAsync check");

        // Brief yield so the exception-handling async continuations after the throw complete
        await Task.Yield();

        // Assert: SwapLabelAsync was NOT called — fail-safe skips on API error
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "Defense 3 API failure must not cause a false-positive agent:error swap");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_SkipsIssueWithTerminalLabel_DespiteStaleListResult()
    {
        UseHostedServiceDefaults();

        // Acceptance criteria #1: verifies current labels before swapping — skips if already terminal.
        // Simulates GitHub API eventual consistency: ListOpenIssuesAsync returns stale agent:in-progress,
        // but GetIssueAsync confirms the issue already has agent:done.
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[] { new IssueSummary
                {
                    Identifier = "1635",
                    Title = "Completed issue (stale list)",
                    Labels = InProgressLabels // stale — GitHub hasn't reflected label swap yet
                }},
                Page = 1,
                PageSize = 100,
                HasMore = false
            });
        // GetIssueAsync returns the ACTUAL current state — already has agent:done
        var getIssueCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockIssueProvider
            .Setup(p => p.GetIssueAsync("1635", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "1635",
                Title = "Completed issue",
                Description = "",
                Labels = new[] { AgentLabels.Done }
            })
            .Callback(() => getIssueCalled.TrySetResult());
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "provider-1")))
            .Returns(mockIssueProvider.Object);

        // Run was already removed from active tracking
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("1635", "provider-1"))
            .Returns(false);

        // Not recently completed (tests the label check path specifically)
        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("1635", "provider-1"))
            .Returns(false);

        // Act: start the service and wait for GetIssueAsync to be called (deterministic sync)
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(getIssueCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(getIssueCalled.Task, "GetIssueAsync should have been called after grace period");

        // Assert: SwapLabelAsync was NOT called — terminal label detected via GetIssueAsync
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Verify GetIssueAsync WAS called (the label check path was exercised).
        // Times.AtLeastOnce because Pass 2 (agent:done scan) also calls GetIssueAsync for this issue.
        mockIssueProvider.Verify(
            p => p.GetIssueAsync("1635", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_SkipsIssueWithEpicReviewLabel_DefenseOne()
    {
        UseHostedServiceDefaults();

        // Verifies that agent:epic-review is recognised as a terminal label by Defense 1.
        // Simulates the narrow crash window: ListOpenIssuesAsync returns stale agent:in-progress,
        // but GetIssueAsync confirms the issue already has agent:epic-review (plan posted, awaiting approval).
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[] { new IssueSummary
                {
                    Identifier = "2481",
                    Title = "Epic issue awaiting plan approval (stale list)",
                    Labels = InProgressLabels // stale — GitHub hasn't reflected label swap yet
                }},
                Page = 1,
                PageSize = 100,
                HasMore = false
            });

        // GetIssueAsync returns the ACTUAL current state — already has agent:epic-review
        var getIssueCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        mockIssueProvider
            .Setup(p => p.GetIssueAsync("2481", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail
            {
                Identifier = "2481",
                Title = "Epic issue awaiting plan approval",
                Description = "",
                Labels = new[] { AgentLabels.EpicReview }
            })
            .Callback(() => getIssueCalled.TrySetResult());
        mockIssueProvider
            .Setup(p => p.DisposeAsync())
            .Returns(ValueTask.CompletedTask);

        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "provider-1")))
            .Returns(mockIssueProvider.Object);

        // Run was already removed from active tracking
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("2481", "provider-1"))
            .Returns(false);

        // Not recently completed (tests the label check path specifically)
        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("2481", "provider-1"))
            .Returns(false);

        // Act: start the service and wait for GetIssueAsync to be called (deterministic sync)
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(getIssueCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(getIssueCalled.Task, "GetIssueAsync should have been called after grace period");

        // TODO: This negative assertion has a potential race — GetIssueAsync has fired but the branch
        // that would call SwapLabelAsync (if EpicReview were absent from TerminalLabels) may not have
        // had time to execute yet, so Times.Never could spuriously pass on a fast machine after a
        // revert. Consider adding a short post-trigger delay or a second TCS signalled after the
        // terminal-label branch resolves to make the assertion reliably deterministic. The same
        // pattern exists in the analogous Sweep_SkipsIssueWithTerminalLabel_DespiteStaleListResult test.

        // Assert: SwapLabelAsync was NOT called — epic-review is a terminal label, Defense 1 skips it
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);

        // Verify GetIssueAsync WAS called (the Defense 1 label check path was exercised).
        // Times.AtLeastOnce because Pass 2 (agent:done scan) also calls GetIssueAsync for this issue.
        mockIssueProvider.Verify(
            p => p.GetIssueAsync("2481", It.IsAny<CancellationToken>()),
            Times.AtLeastOnce);

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Sweep_RecoversGenuinelyOrphanedIssue()
    {
        UseHostedServiceDefaults();

        // Acceptance criteria #4: genuinely orphaned issues (no recent completion, still agent:in-progress)
        // ARE still recovered.
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "500",
            Title = "Genuinely orphaned issue",
            Labels = InProgressLabels
        });

        // No active run
        _mockRunService
            .Setup(r => r.IsIssueBeingProcessed("500", "provider-1"))
            .Returns(false);

        // Not recently completed
        _mockRunService
            .Setup(r => r.WasRecentlyCompleted("500", "provider-1"))
            .Returns(false);

        // GetIssueAsync confirms still in-progress (genuinely stuck)
        // This is already set up by SetupIssueProvider's default GetIssueAsync mock

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "500", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Act
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        // Assert: genuinely orphaned issue IS recovered
        completed.Should().BeSameAs(swapCalled.Task, "Genuinely orphaned issue should be swapped to agent:error");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    // ── Leader gate tests ────────────────────────────────────────────────

    [Fact]
    public async Task LeaderGate_WhenNotLeader_InitialSweepSkipped()
    {
        UseHostedServiceDefaults();

        // Arrange: gate says not-leader — even the initial sweep (after grace period) must be skipped.
        // This verifies the gate lives inside RecoverOrphanedLabelsAsync, not only in the timer loop.
        var mockGate = new Mock<ILeaderGate>();
        mockGate.SetupGet(g => g.IsLeader).Returns(false);
        mockGate.SetupGet(g => g.LeaderToken).Returns(CancellationToken.None);

        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Orphaned issue",
            Labels = InProgressLabels
        });
        _mockRunService.Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>())).Returns(false);

        // Gate: the service loads the sweep interval (not leader-gated) right after the initial
        // sweep returns, so the initial sweep has deterministically finished when this fires.
        var initialSweepDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockConfigClient
            .Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { OrphanedLabelSweepIntervalMinutes = 30 })
            .Callback(() => initialSweepDone.TrySetResult());

        using var service = CreateServiceWithGate(mockGate.Object);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(initialSweepDone.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        completed.Should().BeSameAs(initialSweepDone.Task, "the initial sweep must return after the grace period");

        // Assert: no swap — non-leader skips entirely
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "non-leader must not call SwapLabelAsync on the initial sweep");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaderGate_WhenLeader_SweepRuns()
    {
        UseHostedServiceDefaults();

        // Arrange: gate says is-leader — sweeps must execute normally.
        // Regression guard: verifies the gate check doesn't accidentally block leaders.
        var mockGate = new Mock<ILeaderGate>();
        mockGate.SetupGet(g => g.IsLeader).Returns(true);
        mockGate.SetupGet(g => g.LeaderToken).Returns(CancellationToken.None);

        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Orphaned issue",
            Labels = InProgressLabels
        });
        _mockRunService.Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>())).Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        using var service = CreateServiceWithGate(mockGate.Object);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        completed.Should().BeSameAs(swapCalled.Task, "leader must run the sweep and call SwapLabelAsync");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaderGate_WhenNull_SweepRunsUnconditionally()
    {
        UseHostedServiceDefaults();

        // Arrange: null gate (dev / single-replica) — sweep must run without any gate check.
        // The existing CreateServiceWithGate(null) already passes null, but this test makes the intent explicit.
        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");
        SetupIssueProvider("provider-1", new IssueSummary
        {
            Identifier = "42",
            Title = "Orphaned issue",
            Labels = InProgressLabels
        });
        _mockRunService.Setup(r => r.IsIssueBeingProcessed(It.IsAny<IssueIdentifier>(), It.IsAny<ProviderConfigId>())).Returns(false);

        var swapCalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _mockLabelService
            .Setup(l => l.SwapLabelAsync("provider-1", "42", AgentLabels.Error, LabelTargetKind.Issue, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask)
            .Callback(() => swapCalled.TrySetResult());

        // Explicitly create with null gate
        using var service = CreateServiceWithGate(null);
        await service.StartAsync(_cts.Token);

        var completed = await Task.WhenAny(swapCalled.Task, Task.Delay(TimeSpan.FromSeconds(60)));

        completed.Should().BeSameAs(swapCalled.Task, "null gate must not suppress the sweep");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task LeaderGate_WhenLeadershipLostMidSweep_SweepCancelled()
    {
        UseHostedServiceDefaults();

        // Arrange: leader starts a sweep; LeaderToken is cancelled mid-sweep (simulates leadership loss).
        // The in-flight sweep should be cancelled rather than completing on the former leader.
        using var leaderCts = new CancellationTokenSource();

        var mockGate = new Mock<ILeaderGate>();
        mockGate.SetupGet(g => g.IsLeader).Returns(true);
        mockGate.SetupGet(g => g.LeaderToken).Returns(() => leaderCts.Token);

        SetupTemplateWithProvider("provider-1");
        SetupProviderConfig("provider-1");

        // Issue provider blocks until leaderCts is cancelled — simulates slow API call mid-sweep
        var sweepStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider
            .Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns(async (int _, int _, IReadOnlyList<string>? _, CancellationToken ct) =>
            {
                sweepStarted.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct); // blocks until leadership lost
                return new PagedResult<IssueSummary> { Items = [], Page = 1, PageSize = 100, HasMore = false };
            });
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockIssueProvider.Object);

        using var service = CreateServiceWithGate(mockGate.Object);
        await service.StartAsync(_cts.Token);

        // Wait for sweep to reach the blocking API call
        var started = await Task.WhenAny(sweepStarted.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        started.Should().BeSameAs(sweepStarted.Task, "sweep should have started and reached the API call");

        // Cancel leadership — simulates losing the K8s lease
        await leaderCts.CancelAsync();

        // Give the service time to handle the cancellation
        await Task.Delay(TimeSpan.FromMilliseconds(200));

        // Assert: no swap was ever called — the sweep was cancelled before completion
        _mockLabelService.Verify(
            l => l.SwapLabelAsync(It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(),
                It.IsAny<string>(), It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "sweep cancelled by LeaderToken loss must not produce any label writes");

        _cts.Cancel();
        await service.StopAsync(CancellationToken.None);
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
