using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

// ── Log tests (no MeterListener — no collection needed) ─────────────────────

/// <summary>
/// Tests for the Step 1b sweep-summary log line added to
/// <see cref="HousekeepingService.ExecuteAsync"/>.
/// Uses a custom <see cref="CapturingSink"/> injected into the Serilog logger so that
/// log assertions are deterministic without Moq's generic-overload limitations.
/// </summary>
[Collection("Metrics")]
public class HousekeepingServiceSweepSummaryLogTests
{
    private const string RepoId = "rp-sweep";
    private const string IssueProviderId = "ip-sweep";

    // ── Infrastructure ────────────────────────────────────────────────────────

    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    private static (HousekeepingService Service,
                    Mock<IRepositoryProvider> Provider,
                    Mock<IIssueProvider> Issues,
                    CapturingSink Sink)
        Create()
    {
        var sink = new CapturingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()  // sweep log is Debug — must enable Debug capture
            .WriteTo.Sink(sink)
            .CreateLogger();

        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());

        var staleMock = new Mock<IStaleBranchCleaner>();
        staleMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleMock.Object, reworkMock.Object, logger);
        svc.FireAndForget = task => task;
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        return (svc, providerMock, issuesMock, sink);
    }

    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs,
        bool wasInputTruncated = false)
        => svc.ExecuteAsync(
            repo.Object, RepoId, issues.Object, IssueProviderId,
            prs, wasInputTruncated, 1, false, 60, 25, CancellationToken.None);

    private static PullRequestSummary MakePr(int number)
        => new()
        {
            Number = number, Identifier = number.ToString(), Title = $"PR #{number}",
            Description = string.Empty, Labels = [], BranchName = $"feature/auto-{number}-x",
            TargetBranch = "main", Url = $"https://example.com/pr/{number}", IsDraft = false
        };

    private LogEvent? SweepLog(CapturingSink sink)
        => sink.Events.FirstOrDefault(e =>
            e.MessageTemplate.Text.Contains("sweep") &&
            e.MessageTemplate.Text.Contains("agent:done"));

    // ── TC-L1: Mixed statuses — all five count fields populated ──────────────

    [Fact]
    public async Task SweepLog_MixedStatuses_AllCountsCorrect()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Conflicted);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(4, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(5, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3), MakePr(4), MakePr(5)]);

        var log = SweepLog(sink);
        log.Should().NotBeNull("sweep log must be emitted when PR list is non-empty");
        log!.Properties["Total"].ToString().Should().Be("5");
        log.Properties["Behind"].ToString().Should().Be("1");
        log.Properties["UpToDate"].ToString().Should().Be("1");
        log.Properties["Conflicted"].ToString().Should().Be("1");
        log.Properties["Blocked"].ToString().Should().Be("1");
        log.Properties["Unknown"].ToString().Should().Be("1");
    }

    // ── TC-L2: All UpToDate — zero values are explicit in the log ────────────

    [Fact]
    public async Task SweepLog_AllUpToDate_OtherCountsAreZero()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);

        var log = SweepLog(sink);
        log.Should().NotBeNull();
        log!.Properties["Behind"].ToString().Should().Be("0");
        log.Properties["UpToDate"].ToString().Should().Be("2");
        log.Properties["Conflicted"].ToString().Should().Be("0");
        log.Properties["Blocked"].ToString().Should().Be("0");
        log.Properties["Unknown"].ToString().Should().Be("0");
    }

    // ── TC-L3: Empty PR list — sweep log must NOT be emitted ─────────────────

    [Fact]
    public async Task SweepLog_EmptyPrList_NotEmitted()
    {
        var (svc, provider, issues, sink) = Create();

        await ExecAsync(svc, provider, issues, []);

        SweepLog(sink).Should().BeNull("sweep log must be suppressed when no agent:done PRs are present");
    }

    // ── TC-L4: Re-probe resolves Unknown → log reflects final resolved state ─

    [Fact]
    public async Task SweepLog_UnknownResolvedToBehindOnReprobe_LogShowsBehind()
    {
        var (svc, provider, issues, sink) = Create();
        var callCount = 0;
        // First probe → Unknown; re-probe → Behind
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++callCount == 1 ? PrMergeabilityStatus.Unknown : PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        var log = SweepLog(sink);
        log.Should().NotBeNull();
        // Log fires after full map is built (post-reprobe), so it must show final state
        log!.Properties["Behind"].ToString().Should().Be("1",
            "re-probe resolved Unknown → Behind before the sweep log fires");
        log.Properties["Unknown"].ToString().Should().Be("0");
    }

    // ── TC-L5: Log level is Debug ─────────────────────────────────────────────

    [Fact]
    public async Task SweepLog_LogLevel_IsDebug()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        var log = SweepLog(sink);
        log.Should().NotBeNull();
        log!.Level.Should().Be(LogEventLevel.Debug, "sweep summary is high-frequency; Debug prevents Information noise");
    }

    // ── TC-L6: Emitted exactly once per ExecuteAsync call ────────────────────

    [Fact]
    public async Task SweepLog_EmittedExactlyOncePerCall()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        var sweepLogs = sink.Events.Where(e =>
            e.MessageTemplate.Text.Contains("sweep") &&
            e.MessageTemplate.Text.Contains("agent:done")).ToList();
        sweepLogs.Should().HaveCount(2, "one sweep log per ExecuteAsync call");
    }

    // ── TC-L7: wasInputTruncated=true — "(input truncated)" suffix present ───

    [Fact]
    public async Task SweepLog_TruncatedInput_AppendsTruncatedSuffix()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1)], wasInputTruncated: true);

        var log = SweepLog(sink);
        log.Should().NotBeNull();
        log!.RenderMessage().Should().Contain("input truncated",
            "operator must be warned when counts are understated due to pagination");
    }

    // ── TC-L8: wasInputTruncated=false — no truncation suffix ────────────────

    [Fact]
    public async Task SweepLog_NonTruncatedInput_NoTruncatedSuffix()
    {
        var (svc, provider, issues, sink) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        await ExecAsync(svc, provider, issues, [MakePr(1)], wasInputTruncated: false);

        var log = SweepLog(sink);
        log.Should().NotBeNull();
        log!.RenderMessage().Should().NotContain("truncated");
    }
}

// ── Metric tests (MeterListener — requires [Collection("Metrics")]) ──────────

/// <summary>
/// Tests for the <c>pipeline.housekeeping.pr_evaluated</c> OTel counter emitted
/// by <see cref="HousekeepingService.ExecuteAsync"/> Step 1b.
/// Placed in [Collection("Metrics")] to serialise all MeterListener-based tests and
/// prevent cross-test interference through the process-global static
/// <see cref="PipelineTelemetry.Meter"/>.
/// </summary>
[Collection("Metrics")]
public class HousekeepingServiceSweepSummaryMetricTests
{
    private const string RepoId = "rp-metric";
    private const string IssueProviderId = "ip-metric";

    private static (HousekeepingService Service,
                    Mock<IRepositoryProvider> Provider,
                    Mock<IIssueProvider> Issues)
        Create(HashSet<string>? activeBranches = null)
    {
        var providerMock = new Mock<IRepositoryProvider>();
        var issuesMock = new Mock<IIssueProvider>();
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(activeBranches ?? new HashSet<string>());

        var staleMock = new Mock<IStaleBranchCleaner>();
        staleMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<bool>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<bool>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(
                It.IsAny<IReadOnlyList<PullRequestSummary>>(),
                It.IsAny<IReadOnlyDictionary<int, PrMergeabilityStatus>>(),
                It.IsAny<IReadOnlySet<string>>(), It.IsAny<bool>(),
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<string>(), It.IsAny<KeyValuePair<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = new HousekeepingService(runsMock.Object, staleMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        return (svc, providerMock, issuesMock);
    }

    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs,
        int limit = 1)
        => svc.ExecuteAsync(
            repo.Object, RepoId, issues.Object, IssueProviderId,
            prs, false, limit, false, 60, 25, CancellationToken.None);

    private static PullRequestSummary MakePr(int number)
        => new()
        {
            Number = number, Identifier = number.ToString(), Title = $"PR #{number}",
            Description = string.Empty, Labels = [], BranchName = $"feature/auto-{number}-x",
            TargetBranch = "main", Url = $"https://example.com/pr/{number}", IsDraft = false
        };

    /// <summary>
    /// Sets up a <see cref="MeterListener"/> that captures all
    /// <c>pipeline.housekeeping.pr_evaluated</c> measurements into a list.
    /// Returns a disposable listener and the live measurement list.
    /// </summary>
    private static (MeterListener Listener, List<(long Value, string Status, string RepoId)> Measurements)
        CreateListener()
    {
        var measurements = new List<(long Value, string Status, string RepoId)>();
        _ = PipelineTelemetry.Meter; // pre-touch: ensure all instruments are registered before Start()
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "pipeline.housekeeping.pr_evaluated") return;
            var status = "";
            var repoId = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "mergeability_status") status = tag.Value?.ToString() ?? "";
                if (tag.Key == "repo_provider_id") repoId = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, status, repoId));
        });
        listener.Start();
        return (listener, measurements);
    }

    // ── TC-M1: Single Behind PR — correct label and repo tag ─────────────────

    [Fact]
    public async Task Metric_SingleBehindPr_EmitsBehindWithRepoTag()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)]);
        }

        measurements.Should().ContainSingle(m => m.Status == "behind" && m.RepoId == RepoId,
            "one aggregate Add for the single Behind PR");
        measurements.Single(m => m.Status == "behind").Value.Should().Be(1);
    }

    // ── TC-M2: Mixed statuses — one emission per distinct status ─────────────

    [Fact]
    public async Task Metric_MixedStatuses_OneEmissionPerDistinctStatus()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(2, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3)]);
        }

        measurements.Should().HaveCount(3, "one Add per distinct status: behind, up_to_date, blocked");
        measurements.Should().Contain(m => m.Status == "behind" && m.Value == 1);
        measurements.Should().Contain(m => m.Status == "up_to_date" && m.Value == 1);
        measurements.Should().Contain(m => m.Status == "blocked" && m.Value == 1);
    }

    // ── TC-M3: Zero-count statuses not emitted ───────────────────────────────

    [Fact]
    public async Task Metric_AllUpToDate_OnlyUpToDateEmitted()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)]);
        }

        measurements.Should().ContainSingle(m => m.Status == "up_to_date" && m.RepoId == RepoId,
            "zero-count statuses must not emit — only up_to_date present");
        measurements.Should().NotContain(m => m.Status == "behind");
        measurements.Should().NotContain(m => m.Status == "conflicted");
        measurements.Should().NotContain(m => m.Status == "blocked");
        measurements.Should().NotContain(m => m.Status == "unknown");
    }

    // ── TC-M4: Multiple PRs with same status — aggregate Add(N) not N×Add(1) ─

    [Fact]
    public async Task Metric_MultiplePrsSameStatus_AggregateAdd()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3)]);
        }

        // Three UpToDate PRs → single Add(3), not three Add(1) calls
        measurements.Should().ContainSingle(m => m.Status == "up_to_date" && m.RepoId == RepoId,
            "aggregate Add(N) produces one measurement per status, not N separate Add(1) calls");
        measurements.Single(m => m.Status == "up_to_date" && m.RepoId == RepoId).Value.Should().Be(3);
    }

    // ── TC-M5: Unknown→Behind re-probe — emits "behind", not "unknown" ───────

    [Fact]
    public async Task Metric_UnknownResolvedToBehind_EmitsBehindNotUnknown()
    {
        var (svc, provider, issues) = Create();
        var callCount = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++callCount == 1 ? PrMergeabilityStatus.Unknown : PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)]);
        }

        measurements.Should().NotContain(m => m.Status == "unknown",
            "re-probe resolved Unknown → Behind before metric is emitted");
        measurements.Should().ContainSingle(m => m.Status == "behind" && m.Value == 1);
    }

    // ── TC-M6: Unknown stays Unknown — emits "unknown" ───────────────────────

    [Fact]
    public async Task Metric_UnknownStaysUnknown_EmitsUnknown()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)]);
        }

        measurements.Should().ContainSingle(m => m.Status == "unknown" && m.Value == 1);
    }

    // ── TC-M7: Empty PR list — no metric emissions ───────────────────────────

    [Fact]
    public async Task Metric_EmptyPrList_NoEmissions()
    {
        var (svc, provider, issues) = Create();

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, []);
        }

        measurements.Should().BeEmpty("metric must not emit when there are no agent:done PRs");
    }

    // ── TC-M8: repo_provider_id tag matches parameter value ──────────────────

    [Fact]
    public async Task Metric_RepoProviderIdTag_MatchesParameter()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        var (listener, measurements) = CreateListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)]);
        }

        measurements.Should().ContainSingle(m => m.RepoId == RepoId,
            "repo_provider_id tag must carry the repoProviderId parameter value");
    }

    // ── Helpers for skip_reason and slot_exhausted ────────────────────────────

    private static (MeterListener Listener, List<(long Value, string Reason, string RepoId)> Measurements)
        CreateSkippedListener()
    {
        var measurements = new List<(long Value, string Reason, string RepoId)>();
        _ = PipelineTelemetry.Meter; // pre-touch: ensure all instruments are registered before Start()
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "pipeline.housekeeping.skipped") return;
            var reason = "";
            var repoId = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "skip_reason") reason = tag.Value?.ToString() ?? "";
                if (tag.Key == "repo_provider_id") repoId = tag.Value?.ToString() ?? "";
            }
            measurements.Add((value, reason, repoId));
        });
        listener.Start();
        return (listener, measurements);
    }

    private static (MeterListener Listener, List<string> Fires) CreateSlotExhaustedListener()
    {
        var fires = new List<string>(); // repo_provider_id per increment
        // Pre-touch the static meter so all instruments (including slot_exhausted) are registered
        // before listener.Start() is called. Without this, on a fresh test process where
        // PipelineTelemetry hasn't been initialised yet, instruments are created during ExecAsync
        // on a thread-pool continuation, and the InstrumentPublished/EnableMeasurementEvents path
        // can race against the first Add call on Linux CI runners.
        _ = PipelineTelemetry.Meter;
        var listener = new MeterListener();
        // Match all CodingAgent.Pipeline instruments (same pattern as HousekeepingPrOutcomeTests)
        // rather than a name-exact filter, so the callback is registered before any Add fires
        // regardless of static-initialisation order.
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (instrument.Name != "pipeline.housekeeping.slot_exhausted") return;
            var repoId = "";
            foreach (var tag in tags)
                if (tag.Key == "repo_provider_id") repoId = tag.Value?.ToString() ?? "";
            fires.Add(repoId);
        });
        listener.Start();
        return (listener, fires);
    }

    // ── TC-M9: skip_reason=active_run ────────────────────────────────────────

    [Fact]
    public async Task Metric_SkipReason_ActiveRun_TaggedCorrectly()
    {
        var pr = MakePr(1); // branch = "feature/auto-1-x"
        var (svc, provider, issues) = Create(activeBranches: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { pr.BranchName });
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);

        var (listener, measurements) = CreateSkippedListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [pr]);
        }

        measurements.Should().ContainSingle(
            m => m.Reason == PipelineTelemetry.HousekeepingSkipReasons.ActiveRun && m.RepoId == RepoId,
            "Behind PR whose branch has an active run must emit skip_reason=active_run");
    }

    // ── TC-M10: skip_reason=in_flight ─────────────────────────────────────────

    [Fact]
    public async Task Metric_SkipReason_InFlight_TaggedCorrectly()
    {
        var (svc, provider, issues) = Create();
        // Cycle 1: trigger PR#1 (Behind) → enters inFlight
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        await ExecAsync(svc, provider, issues, [MakePr(1)], limit: 2);

        // Cycle 2: PR#1 is Blocked — EvictInFlightSlots keeps it in inFlight
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Blocked);

        var (listener, measurements) = CreateSkippedListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)], limit: 2);
        }

        measurements.Should().ContainSingle(
            m => m.Reason == PipelineTelemetry.HousekeepingSkipReasons.InFlight && m.RepoId == RepoId,
            "PR occupying an in-flight slot (still Blocked) must emit skip_reason=in_flight");
    }

    // ── TC-M11: skip_reason=cooldown ──────────────────────────────────────────

    [Fact]
    public async Task Metric_SkipReason_Cooldown_TaggedCorrectly()
    {
        var (svc, provider, issues) = Create();
        var clock = DateTimeOffset.UtcNow;
        svc.UtcNow = () => clock;
        svc.TriggerCooldown = TimeSpan.FromMinutes(25);

        // Cycle 1: trigger PR#1 → records _lastTriggeredAt
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
        await ExecAsync(svc, provider, issues, [MakePr(1)]);

        // Cycle 2: PR#1 Behind again, clock NOT advanced — within cooldown
        // EvictInFlightSlots evicts (Behind is not Blocked/Unknown), so in_flight guard won't fire

        var (listener, measurements) = CreateSkippedListener();
        using (listener)
        {
            await ExecAsync(svc, provider, issues, [MakePr(1)]);
        }

        measurements.Should().ContainSingle(
            m => m.Reason == PipelineTelemetry.HousekeepingSkipReasons.Cooldown && m.RepoId == RepoId,
            "Behind PR re-attempted within TriggerCooldown window must emit skip_reason=cooldown");
    }

    // ── TC-M12: slot_exhausted fires once when concurrency cap hit ───────────

    [Fact]
    public async Task Metric_SlotExhausted_FiresOnceWhenCapReached()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, fires) = CreateSlotExhaustedListener();
        using (listener)
        {
            // limit=1, 2 Behind PRs: first fills the slot, second hits inFlight.Count >= limit → slot_exhausted
            await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2)], limit: 1);
        }

        fires.Should().ContainSingle(repoId => repoId == RepoId,
            "slot_exhausted must fire exactly once when the concurrency cap is hit mid-loop");
    }
}
