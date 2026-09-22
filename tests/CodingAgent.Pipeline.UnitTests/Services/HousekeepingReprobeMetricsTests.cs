using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Moq;
using Serilog;
using Xunit;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that <see cref="HousekeepingService"/> emits the correct OTel metrics for the
/// re-probe pass: <c>pipeline.housekeeping.reprobe_triggered</c> and
/// <c>pipeline.housekeeping.reprobe_resolved</c>.
///
/// Uses <c>MeterListener</c> against the static <see cref="PipelineTelemetry.Meter"/> because
/// <see cref="HousekeepingService"/> calls the static counters directly. Placed in
/// <see cref="MetricsTestCollection"/> so these tests run serially and do not bleed emissions
/// into parallel test classes.
/// </summary>
[Collection("Metrics")]
public class HousekeepingReprobeMetricsTests
{
    private const string RepoId = "rp-metrics";
    private const string IssueProviderId = "ip-metrics";

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static PullRequestSummary MakePr(int number) => new()
    {
        Number = number,
        Identifier = number.ToString(),
        Title = $"PR #{number}",
        Description = string.Empty,
        Labels = Array.Empty<string>(),
        BranchName = $"feature/pr-{number}",
        TargetBranch = "main",
        Url = $"https://example.com/pr/{number}",
        IsDraft = false
    };

    private static (HousekeepingService Service,
                    Mock<IRepositoryProvider> ProviderMock,
                    Mock<IIssueProvider> IssueProviderMock)
        Create()
    {
        var runsMock = new Mock<IOrchestratorRunService>();
        runsMock.Setup(r => r.GetActiveRunBranchesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new HashSet<string>());

        var staleBranchMock = new Mock<IStaleBranchCleaner>();
        staleBranchMock.Setup(s => s.RunIfDueAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<IIssueProvider>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<string>(),
                It.IsAny<KeyValuePair<string, object?>>(), It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
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

        var providerMock = new Mock<IRepositoryProvider>();
        providerMock.Setup(p => p.ListOpenPullRequestsAsync(
                        It.IsAny<int>(), It.IsAny<int>(),
                        It.Is<IReadOnlyList<string>?>(l => l == null),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<PullRequestSummary>
                    {
                        Items = Array.Empty<PullRequestSummary>().AsReadOnly(),
                        Page = 1,
                        PageSize = 100,
                        HasMore = false
                    });

        var svc = new HousekeepingService(runsMock.Object, staleBranchMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        return (svc, providerMock, new Mock<IIssueProvider>());
    }

    private static Task ExecAsync(
        HousekeepingService svc, Mock<IRepositoryProvider> repo, Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> prs, int limit = 1)
        => svc.ExecuteAsync(repo.Object, RepoId, issues.Object, IssueProviderId,
            prs, limit, false, 60, 25, CancellationToken.None);

    private (System.Diagnostics.Metrics.MeterListener Listener,
             System.Collections.Concurrent.ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> Measurements)
        CreateMeterListener()
    {
        var measurements = new System.Collections.Concurrent.ConcurrentBag<(string, long, KeyValuePair<string, object?>[])>();
        var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            // Filter to this class's own emissions by repo_provider_id to prevent parallel test
            // contamination: HousekeepingServiceTests runs concurrently and emits on the same
            // static Meter with a different RepoId ("rp-1"). Without the filter, assertions
            // like ContainSingle() would see 2 entries instead of 1.
            var tagsArr = tags.ToArray();
            if (tagsArr.Any(t => t.Key == "repo_provider_id" && Equals(t.Value, RepoId)))
                measurements.Add((instrument.Name, value, tagsArr));
        });
        listener.Start();
        return (listener, measurements);
    }

    // ── HousekeepingReprobeTriggered ──────────────────────────────────────────

    [Fact]
    public async Task HousekeepingReprobeTriggered_EmittedOnce_WhenAnyPrReturnsUnknown()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        listener.Dispose();

        var triggered = measurements.Where(m => m.Name == "pipeline.housekeeping.reprobe_triggered").ToList();
        triggered.Should().ContainSingle("reprobe_triggered must fire exactly once per cycle batch");
        triggered[0].Value.Should().Be(1L);
        triggered[0].Tags.Should().Contain(
            new KeyValuePair<string, object?>("repo_provider_id", RepoId),
            "reprobe_triggered must be tagged with the repo provider id");
    }

    [Fact]
    public async Task HousekeepingReprobeTriggered_NotEmitted_WhenNoPrReturnsUnknown()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        listener.Dispose();

        measurements.Should().NotContain(m => m.Name == "pipeline.housekeeping.reprobe_triggered",
            "reprobe_triggered must not fire when no PR returned Unknown on the first probe");
    }

    [Fact]
    public async Task HousekeepingReprobeTriggered_EmittedOnce_EvenWithMultipleUnknownPrs()
    {
        var (svc, provider, issues) = Create();
        // Three PRs all return Unknown — still only one batch trigger
        foreach (var n in new[] { 1, 2, 3 })
            provider.Setup(p => p.IsPullRequestBehindBaseAsync(n, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Unknown);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1), MakePr(2), MakePr(3)], limit: 3);
        listener.Dispose();

        var triggered = measurements.Where(m => m.Name == "pipeline.housekeeping.reprobe_triggered").ToList();
        triggered.Should().ContainSingle(
            "reprobe_triggered must fire once per cycle regardless of how many Unknown PRs there are");
    }

    // ── HousekeepingReprobeResolved ───────────────────────────────────────────

    [Fact]
    public async Task HousekeepingReprobeResolved_EmittedWithCorrectTag_WhenPrResolves()
    {
        var (svc, provider, issues) = Create();

        // PR resolves Unknown → Behind on re-probe
        var calls = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++calls == 1 ? PrMergeabilityStatus.Unknown : PrMergeabilityStatus.Behind);
        provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        listener.Dispose();

        var resolved = measurements.Where(m => m.Name == "pipeline.housekeeping.reprobe_resolved").ToList();
        resolved.Should().ContainSingle("reprobe_resolved must fire once for the PR that resolved");
        resolved[0].Value.Should().Be(1L);
        resolved[0].Tags.Should().Contain(
            new KeyValuePair<string, object?>("resolved_state", "behind"),
            "resolved_state tag must be 'behind' (snake_case, not enum name)");
        resolved[0].Tags.Should().Contain(
            new KeyValuePair<string, object?>("repo_provider_id", RepoId));
    }

    [Fact]
    public async Task HousekeepingReprobeResolved_NotEmitted_WhenPrStaysUnknown()
    {
        var (svc, provider, issues) = Create();
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(PrMergeabilityStatus.Unknown);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        listener.Dispose();

        measurements.Should().NotContain(m => m.Name == "pipeline.housekeeping.reprobe_resolved",
            "reprobe_resolved must not fire when re-probe still returns Unknown");
    }

    [Theory]
    [InlineData(PrMergeabilityStatus.Behind, "behind")]
    [InlineData(PrMergeabilityStatus.UpToDate, "up_to_date")]
    [InlineData(PrMergeabilityStatus.Conflicted, "conflicted")]
    [InlineData(PrMergeabilityStatus.Blocked, "blocked")]
    public async Task HousekeepingReprobeResolved_ResolvedStateTag_UsesSnakeCaseValues(
        PrMergeabilityStatus reprobeResult, string expectedTag)
    {
        var (svc, provider, issues) = Create();

        var calls = 0;
        provider.Setup(p => p.IsPullRequestBehindBaseAsync(1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => ++calls == 1 ? PrMergeabilityStatus.Unknown : reprobeResult);

        // For Behind: set up UpdatePullRequestBranchAsync so Step 6b doesn't throw
        if (reprobeResult == PrMergeabilityStatus.Behind)
            provider.Setup(p => p.UpdatePullRequestBranchAsync(1, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        // For Conflicted: set up ExtractLinkedIssuesAsync to avoid null-ref in TriggerReworkAsync
        if (reprobeResult == PrMergeabilityStatus.Conflicted)
            provider.Setup(p => p.ExtractLinkedIssuesAsync(1, It.IsAny<CancellationToken>()))
                    .ReturnsAsync((IReadOnlyList<string>)[]);

        var (listener, measurements) = CreateMeterListener();

        await ExecAsync(svc, provider, issues, [MakePr(1)]);
        listener.Dispose();

        var resolved = measurements.Where(m => m.Name == "pipeline.housekeeping.reprobe_resolved").ToList();
        resolved.Should().ContainSingle();
        resolved[0].Tags.Should().Contain(
            new KeyValuePair<string, object?>("resolved_state", expectedTag),
            $"resolved_state for {reprobeResult} must be '{expectedTag}' (snake_case)");
    }
}
