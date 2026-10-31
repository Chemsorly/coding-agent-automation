using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Scheduler.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.UnitTests;

/// <summary>
/// Unit tests for <see cref="AutoConsolidationService.RunOnceAsync"/>.
/// Uses <see cref="Mock{T}"/> of <see cref="ConsolidationStalenessService"/> (the class is
/// non-sealed with virtual methods) to control counter values without network I/O.
/// </summary>
public sealed class AutoConsolidationServiceTests
{
    // ── Fixed template / project ────────────────────────────────────────────

    private static readonly PipelineJobTemplate TemplateBp1 = new()
    {
        Id = "tmpl-auto-1",
        Name = "Brain Repo 1",
        IssueProviderId = "ip-auto-1",
        RepoProviderId = "rp-auto-1",
        BrainProviderId = "bp-auto-1",
        Enabled = true,
    };

    private static readonly PipelineJobTemplate TemplateBp1B = new()
    {
        Id = "tmpl-auto-1b",
        Name = "Brain Repo 1B — shares brain with 1",
        IssueProviderId = "ip-auto-1b",
        RepoProviderId = "rp-auto-1b",
        BrainProviderId = "bp-auto-1",
        Enabled = true,
    };

    private static readonly PipelineJobTemplate TemplateRefactoring = new()
    {
        Id = "tmpl-auto-ref",
        Name = "Refactoring Repo",
        IssueProviderId = "ip-auto-ref",
        RepoProviderId = "rp-auto-ref",
        Enabled = true,
    };

    private static readonly PipelineProject DefaultProject = new()
    {
        Id = WellKnownIds.DefaultProjectId,
        Name = "Default",
        Enabled = true,
        TemplateIds = [TemplateBp1.Id, TemplateBp1B.Id, TemplateRefactoring.Id],
    };

    // ── Mocks ───────────────────────────────────────────────────────────────

    private readonly Mock<IConsolidationService> _consolidation = new(MockBehavior.Default);
    private readonly Mock<ConsolidationStalenessService> _staleness = new(MockBehavior.Default);
    private readonly Mock<IPipelineConfigStore> _configStore = new(MockBehavior.Default);
    private readonly Mock<IPipelineApiConfigClient> _configClient = new(MockBehavior.Default);
    private readonly Mock<ILeaderGate> _leaderGate = new(MockBehavior.Default);
    private readonly Mock<ILogger> _logger = new(MockBehavior.Default);

    public AutoConsolidationServiceTests()
    {
        // Logger forwarding
        _logger.Setup(l => l.ForContext<AutoConsolidationService>()).Returns(_logger.Object);
        _logger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>())).Returns(_logger.Object);

        // Default: not paused, counters return 0
        _staleness.Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _staleness.Setup(s => s.CountClosedIssuesSinceLastScanAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);
        _staleness.Setup(s => s.CountOpenScanIssuesAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        // Default: trigger returns a successful result
        _consolidation.Setup(c => c.TriggerAutomaticAsync(
                It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationTriggerResult(
                RunId: Guid.NewGuid().ToString(),
                Type: ConsolidationRunType.BrainConsolidation,
                TemplateId: "tmpl-auto-1",
                TemplateName: "Brain Repo 1",
                ProjectId: DefaultProject.Id,
                ProjectName: DefaultProject.Name,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        // Default: one project, three templates
        _configClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([DefaultProject]);
        _configClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([TemplateBp1, TemplateBp1B, TemplateRefactoring]);

        // Default leader = true
        _leaderGate.SetupGet(g => g.IsLeader).Returns(true);
    }

    private AutoConsolidationService CreateSut(PipelineConfiguration? config = null)
    {
        var cfg = config ?? new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 5,
            AutoRefactoringScanAfterClosedIssues = 3,
        };
        _configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(cfg);

        // Build a scope factory backed by the test mock so RunOnceAsync resolves the
        // same mock object when it creates a scope and calls GetRequiredService.
        var services = new ServiceCollection();
        services.AddSingleton(_configClient.Object);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return new AutoConsolidationService(
            _consolidation.Object,
            _staleness.Object,
            _configStore.Object,
            scopeFactory,
            _leaderGate.Object,
            _logger.Object);
    }

    // ── All thresholds zero ────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_AllThresholdsZero_TriggersNothing()
    {
        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 0,
            AutoRefactoringScanAfterClosedIssues = 0,
        });

        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _staleness.Verify(
            s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
        _staleness.Verify(
            s => s.CountClosedIssuesSinceLastScanAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── Brain consolidation ────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_BrainCounterReachesThreshold_TriggersBrainConsolidationOncePerBrain()
    {
        // TemplateBp1 and TemplateBp1B share "bp-auto-1".
        // With count >= threshold, only ONE TriggerAutomaticAsync call should be made (for the first template).
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync("bp-auto-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // equals the threshold of 5

        TemplateId? capturedTemplateId = null;
        _consolidation.Setup(c => c.TriggerAutomaticAsync(
                ConsolidationRunType.BrainConsolidation, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .Callback<ConsolidationRunType, TemplateId?, CancellationToken>((_, tid, _) => capturedTemplateId = tid)
            .ReturnsAsync((ConsolidationTriggerResult?)new ConsolidationTriggerResult(
                RunId: Guid.NewGuid().ToString(),
                Type: ConsolidationRunType.BrainConsolidation,
                TemplateId: TemplateBp1.Id,
                TemplateName: TemplateBp1.Name,
                ProjectId: DefaultProject.Id,
                ProjectName: DefaultProject.Name,
                StartedAtUtc: DateTimeOffset.UtcNow,
                WorkItemId: null));

        var sut = CreateSut();
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(ConsolidationRunType.BrainConsolidation, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "one trigger per brain, not per template sharing the brain");

        capturedTemplateId.Should().NotBeNull();
        capturedTemplateId!.Value.Value.Should().Be(TemplateBp1.Id,
            "the first template of the group (in TemplateIds order) is used");
    }

    [Fact]
    public async Task RunOnce_ReadOnlyBrain_IsNotTriggered()
    {
        // Template with BrainReadOnly = true should be excluded
        var readOnlyTemplate = TemplateBp1 with { BrainReadOnly = true };
        _configClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([readOnlyTemplate]);

        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        var sut = CreateSut();
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(ConsolidationRunType.BrainConsolidation, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "read-only brain templates should be excluded from automatic brain consolidation");
    }

    [Fact]
    public async Task RunOnce_LastRunFailed_SkipsScope()
    {
        _staleness.Setup(s => s.IsPausedAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        var sut = CreateSut();
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a paused scope must not trigger");

        // TODO: this test does not assert that count methods (CountBrainRunsSinceLastConsolidationAsync,
        // CountClosedIssuesSinceLastScanAsync) are
        // Times.Never when paused, because the current implementation fetches counts BEFORE calling
        // IsPausedAsync (inside TryTriggerIfThresholdReachedAsync). Adding Times.Never assertions for
        // count methods would fail until the ordering is fixed (see TODO in TryTriggerBrainScopeAsync
        // and TryTriggerRefactoringDetectionScopeAsync).
    }

    // ── Refactoring scan ───────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_OpenScanIssuesAtLimit_SkipsRefactoringScan()
    {
        _staleness.Setup(s => s.CountOpenScanIssuesAsync(
                It.Is<PipelineJobTemplate>(t => t.Id == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // equals MaxRefactoringProposals default (3)

        _staleness.Setup(s => s.CountClosedIssuesSinceLastScanAsync(
                It.Is<PipelineJobTemplate>(t => t.Id == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(10); // above threshold

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoRefactoringScanAfterClosedIssues = 3,
            MaxRefactoringProposals = 3,
        });
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(ConsolidationRunType.RefactoringDetection, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "open scan issues at the limit suppress automatic scan triggers");
    }

    [Fact]
    public async Task RunOnce_ClosedIssuesReachThreshold_TriggersRefactoringScan()
    {
        _staleness.Setup(s => s.CountClosedIssuesSinceLastScanAsync(
                It.Is<PipelineJobTemplate>(t => t.Id == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(3); // equals threshold

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoRefactoringScanAfterClosedIssues = 3,
        });
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(
                ConsolidationRunType.RefactoringDetection,
                It.Is<TemplateId?>(t => t.HasValue && t.Value.Value == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── Harness suggestions ────────────────────────────────────────────────

    // ── Error isolation ────────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_OneTemplateThrows_OtherTemplatesStillChecked()
    {
        // The throwing template is listed FIRST so that TemplateRefactoring is only processed
        // after error handling for the first template completes. If the catch block incorrectly
        // used break/return/rethrow, TemplateRefactoring would never be reached and the Times.Once
        // assertion below would fail — correctly detecting the regression.
        var throwingTemplate = new PipelineJobTemplate
        {
            Id = "tmpl-auto-throws",
            Name = "Throwing Repo",
            IssueProviderId = "ip-throws",
            RepoProviderId = "rp-throws",
            Enabled = true,
        };

        var projectWithTwo = DefaultProject with
        {
            TemplateIds = [throwingTemplate.Id, TemplateRefactoring.Id],
        };

        _configClient.Setup(c => c.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([projectWithTwo]);
        _configClient.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync([throwingTemplate, TemplateRefactoring]);

        _staleness.Setup(s => s.CountClosedIssuesSinceLastScanAsync(
                It.Is<PipelineJobTemplate>(t => t.Id == throwingTemplate.Id),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated tracker failure"));

        _staleness.Setup(s => s.CountClosedIssuesSinceLastScanAsync(
                It.Is<PipelineJobTemplate>(t => t.Id == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // above threshold

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoRefactoringScanAfterClosedIssues = 3,
        });

        // Should not throw despite the first template erroring
        await sut.RunOnceAsync(CancellationToken.None);

        // TODO: also assert that IsPausedAsync is not called after count throws (ordering issue:
        // counts are currently fetched before IsPausedAsync in TryTriggerIfThresholdReachedAsync).

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(
                ConsolidationRunType.RefactoringDetection,
                It.Is<TemplateId?>(t => t.HasValue && t.Value.Value == TemplateRefactoring.Id),
                It.IsAny<CancellationToken>()),
            Times.Once,
            "TemplateRefactoring's trigger must still fire despite the preceding template throwing");
    }

    // ── Leader gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task RunOnce_NotLeader_TriggersNothing()
    {
        // RunOnceAsync itself does NOT check leader status — that check is in ExecuteAsync.
        // However, by passing _leaderGate with IsLeader=false into AutoConsolidationService,
        // the guard fires before RunOnceAsync is called. We test RunOnceAsync directly here
        // with leader=true and verify no leaks, then confirm IsLeader=false is what prevents
        // dispatch by verifying the gate is checked during ExecuteAsync in BackgroundService.
        // This test covers the RunOnceAsync + leader check path together:
        _leaderGate.SetupGet(g => g.IsLeader).Returns(false);

        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(10);

        // Execute full BackgroundService loop with leader=false
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var services1 = new ServiceCollection();
        services1.AddSingleton(_configClient.Object);
        var scopeFactory1 = services1.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new AutoConsolidationService(
            _consolidation.Object,
            _staleness.Object,
            _configStore.Object,
            scopeFactory1,
            _leaderGate.Object,
            _logger.Object,
            interval: TimeSpan.FromMilliseconds(10));

        // Config is set up for all-non-zero thresholds so triggers would fire if leader check was bypassed
        _configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                AutoBrainConsolidationAfterRuns = 1,
                AutoRefactoringScanAfterClosedIssues = 1,
            });

        try
        {
            await sut.StartAsync(cts.Token);
            await Task.Delay(150, cts.Token);
        }
        catch (OperationCanceledException) { /* expected */ }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "non-leader should never trigger consolidations");
    }

    // ── Error isolation — per-section catch block coverage ─────────────────

    [Fact]
    public async Task RunOnce_BrainScopeThrows_DoesNotPropagateAndLogsWarning()
    {
        // Exercises the catch (Exception ex) block inside TryTriggerBrainScopeAsync.
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync("bp-auto-1", It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated brain staleness failure"));

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 1,
        });

        // Must not throw despite the brain scope error
        await sut.RunOnceAsync(CancellationToken.None);

        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a throwing brain scope must not trigger consolidation");
    }

    [Fact]
    public async Task RunOnce_TriggerReturnsNull_DoesNotThrow()
    {
        // Exercises the "result is null" branch inside TryTriggerIfThresholdReachedAsync.
        // TriggerAutomaticAsync returns null when a run is already in progress or rejected.
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync("bp-auto-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // meets threshold

        _consolidation.Setup(c => c.TriggerAutomaticAsync(
                ConsolidationRunType.BrainConsolidation, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationTriggerResult?)null);

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 5,
        });

        await sut.RunOnceAsync(CancellationToken.None);

        // Confirms the trigger was actually called (threshold was crossed and null result was handled).
        _consolidation.Verify(
            c => c.TriggerAutomaticAsync(
                ConsolidationRunType.BrainConsolidation,
                It.IsAny<TemplateId?>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ── WorkItemRefusedException handler ──────────────────────────────────

    [Fact]
    public async Task RunOnce_TriggerThrowsWorkItemRefusedException_DoesNotPropagateAndLogsWarning()
    {
        // Exercises the catch (WorkItemRefusedException ex) block in TryTriggerIfThresholdReachedAsync
        // (L268–L275). When the API refuses a run (no agent route, or one that cannot run), the
        // exception must be absorbed and a warning logged — the loop must NOT exit.
        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync("bp-auto-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(5); // meets the threshold of 5

        _consolidation.Setup(c => c.TriggerAutomaticAsync(
                ConsolidationRunType.BrainConsolidation, It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkItemRefusedException("No agent route configured for this template"));

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 5,
            AutoRefactoringScanAfterClosedIssues = 0,
        });

        // Must complete without throwing
        await sut.RunOnceAsync(CancellationToken.None);

        // A warning with the refusal reason must have been logged.
        // Production call: _logger.Warning(ex, "...{Type}...{Scope}...{RefusalText}", type, scopeKey, ex.Message)
        // resolves to Warning<ConsolidationRunType, string, string>(Exception, string, T0, T1, T2).
        _logger.Verify(l => l.Warning(
                It.IsAny<Exception>(),
                It.IsAny<string>(),
                It.IsAny<ConsolidationRunType>(),
                It.IsAny<string>(),
                It.IsAny<string>()),
            Times.Once);
    }

    // ── OCE re-throw — TryTriggerBrainScopeAsync ──────────────────────────

    [Fact]
    public async Task RunOnce_BrainScopeCancelledMidCall_PropagatesOperationCanceledException()
    {
        // Exercises the catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // guard in TryTriggerBrainScopeAsync (L177–L179). A cancellation that fires inside a brain
        // scope must propagate to the caller rather than being silently swallowed by the general
        // catch (Exception ex) block below it.
        //
        // The when-filter checks ct.IsCancellationRequested at the moment the exception is caught,
        // so the token MUST be cancelled before the faulted task is returned (not after).
        using var cts = new CancellationTokenSource();

        _staleness.Setup(s => s.CountBrainRunsSinceLastConsolidationAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns<string, CancellationToken>((_, _) =>
            {
                cts.Cancel(); // mark as cancelled BEFORE throwing
                return Task.FromException<int>(new OperationCanceledException(cts.Token));
            });

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 1,
            AutoRefactoringScanAfterClosedIssues = 0,
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.RunOnceAsync(cts.Token));
    }

    // ── OCE re-throw — TryTriggerRefactoringDetectionScopeAsync ───────────

    [Fact]
    public async Task RunOnce_RefactoringScopeCancelledMidCall_PropagatesOperationCanceledException()
    {
        // Exercises the catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // guard in TryTriggerRefactoringDetectionScopeAsync (L223–L225). Same invariant as the brain
        // scope: a timely cancellation must NOT be swallowed by the general exception handler.
        //
        // CountOpenScanIssuesAsync is the first async call in TryTriggerRefactoringDetectionScopeAsync,
        // so the cancellation fires there (before any count threshold is evaluated).
        using var cts = new CancellationTokenSource();

        _staleness.Setup(s => s.CountOpenScanIssuesAsync(It.IsAny<PipelineJobTemplate>(), It.IsAny<CancellationToken>()))
            .Returns<PipelineJobTemplate, CancellationToken>((_, _) =>
            {
                cts.Cancel(); // mark as cancelled BEFORE throwing
                return Task.FromException<int>(new OperationCanceledException(cts.Token));
            });

        var sut = CreateSut(new PipelineConfiguration
        {
            AutoBrainConsolidationAfterRuns = 0,
            AutoRefactoringScanAfterClosedIssues = 1,
        });

        await Assert.ThrowsAsync<OperationCanceledException>(() => sut.RunOnceAsync(cts.Token));
    }

    // ── BackgroundService loop — ExecuteAsync exception handler and graceful-exit ──

    // NOTE: The `if (!await timer.WaitForNextTickAsync(stoppingToken)) break;` path at L61 is not
    // covered here. PeriodicTimer is sealed and its WaitForNextTickAsync only returns false when
    // Dispose() is called on the timer while awaiting — a path reachable only from within
    // ExecuteAsync itself (the timer is a `using var` local). BackgroundService.StopAsync cancels
    // the stopping token, which throws OperationCanceledException (L65–68), not returns false (L61).
    //
    // Covering L61 requires either:
    //   (a) Making the timer injectable (a production code change), or
    //   (b) Reflection to access and dispose the private local.
    //
    // The issue acceptance criteria prohibit production file changes (AC4). Since AC3 sub-requirement
    // (cover L61) and AC4 (no production changes) are irreconcilable, L61 remains uncovered.
    // The equivalent graceful-exit behaviour IS covered via L65–68 by RunOnce_NotLeader_TriggersNothing
    // and ExecuteAsync_RunOnceThrowsUnexpectedly_LoopContinuesOnNextTick (both call StopAsync, which
    // exits the loop via the OCE path). A follow-up issue should request extracting PeriodicTimer
    // creation behind a factory to enable direct L61 coverage.

    [Fact]
    public async Task ExecuteAsync_RunOnceThrowsUnexpectedly_LoopContinuesOnNextTick()
    {
        // Exercises the catch (Exception ex) handler in ExecuteAsync (L82–L85) which logs a warning
        // and retries on the next tick. If the handler were missing (or used break/return/rethrow),
        // the service would exit after the first exception and the signal below would never fire.
        //
        // Uses BackgroundServiceRunner + CallSignal to avoid timing-based assertions that flake on
        // resource-limited CI runners.
        var signal = new CallSignal(count: 1);
        var firstCall = true;

        _configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .Returns<CancellationToken>(_ =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    // First tick: simulate an unexpected error to exercise the catch (Exception) path.
                    return Task.FromException<PipelineConfiguration>(
                        new InvalidOperationException("Simulated unexpected error on first tick"));
                }

                // Subsequent ticks: succeed and signal that the loop continued.
                signal.Hit();
                return Task.FromResult(new PipelineConfiguration
                {
                    AutoBrainConsolidationAfterRuns = 0,
                    AutoRefactoringScanAfterClosedIssues = 0,
                });
            });

        var services2 = new ServiceCollection();
        services2.AddSingleton(_configClient.Object);
        var scopeFactory2 = services2.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        var sut = new AutoConsolidationService(
            _consolidation.Object,
            _staleness.Object,
            _configStore.Object,
            scopeFactory2,
            _leaderGate.Object,
            _logger.Object,
            interval: TimeSpan.FromMilliseconds(10));

        await BackgroundServiceRunner.RunUntilAsync(
            sut,
            signal.Reached,
            "ExecuteAsync loop must continue after an unexpected exception — catch (Exception ex) at L82 was probably removed or changed to rethrow");

        // Confirm the loop ran at least twice (once throwing, once succeeding)
        _configStore.Verify(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()), Times.AtLeast(2));

        // Warning was emitted on the throwing tick.
        // Production call: _logger.Warning(ex, "AutoConsolidationService: unexpected error during tick...")
        // — no template properties, so this is the non-generic two-arg overload Warning(Exception, string).
        _logger.Verify(l => l.Warning(It.IsAny<Exception>(), It.IsAny<string>()), Times.AtLeastOnce());
    }
}
