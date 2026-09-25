using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
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
/// Tests for <c>pipeline.pull_requests.closed</c> (Counter) and
/// <c>pipeline.pull_requests.time_to_merge</c> (Histogram) emission by
/// <see cref="HousekeepingService"/>.
///
/// Verifies:
/// 1. Counter fires with correct <c>outcome</c> tag when a PR leaves the <c>agent:done</c> list.
/// 2. Deduplication: the counter fires at most once per PR across multiple poll cycles.
/// 3. Histogram fires for <c>merged</c> PRs with a positive seconds value.
/// 4. Histogram is NOT emitted for <c>closed_unmerged</c> PRs.
/// 5. Histogram is NOT emitted when <c>CreatedAt</c> is null.
/// 6. Neither counter nor histogram fires when the outcome seam returns null.
///
/// Must be in <c>[Collection("Metrics")]</c> to serialize execution against the static
/// <see cref="PipelineTelemetry.Meter"/>. <see cref="HousekeepingServiceTests"/> runs
/// concurrently and emits to the same meter — without serialization, measurements
/// from that class bleed into assertions here.
/// </summary>
[Collection("Metrics")]
public class HousekeepingPrOutcomeTests
{
    private const string RepoId = "rp-pr-outcome";
    private const string IssueProviderId = "ip-pr-outcome";

    private static PullRequestSummary MakePr(int number, DateTime? createdAt = null) => new()
    {
        Number = number,
        Identifier = number.ToString(),
        Title = $"PR #{number}",
        Description = string.Empty,
        Labels = Array.Empty<string>(),
        BranchName = $"agent/pr-{number}",
        TargetBranch = "main",
        Url = $"https://github.com/owner/repo/pull/{number}",
        IsDraft = false,
        CreatedAt = createdAt
    };

    private static (HousekeepingService Service, Mock<IRepositoryProvider> ProviderMock, Mock<IIssueProvider> IssueMock)
        Create(string? outcomeOverride = null, Func<IRepositoryProvider, int, CancellationToken, Task<string?>>? outcomeSeam = null)
    {
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

        var providerMock = new Mock<IRepositoryProvider>();
        // IsPullRequestBehindBaseAsync must return UpToDate for PRs that are "current" (in the list).
        // For absent PRs it won't be called (they're not in agentDonePrs).
        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        var svc = new HousekeepingService(runsMock.Object, staleMock.Object, reworkMock.Object, Log.Logger);
        svc.FireAndForget = task => task;
        svc.MergeabilityReprobeDelay = TimeSpan.Zero;

        // Install the outcome seam.
        if (outcomeSeam is not null)
            svc.GetPrOutcomeAsync = outcomeSeam;
        else if (outcomeOverride is not null)
            svc.GetPrOutcomeAsync = (_, _, _) => Task.FromResult<string?>(outcomeOverride);
        else
            // Default: return null (unknown outcome — no metric emitted)
            svc.GetPrOutcomeAsync = (_, _, _) => Task.FromResult<string?>(null);

        return (svc, providerMock, new Mock<IIssueProvider>());
    }

    private static Task ExecAsync(
        HousekeepingService svc,
        Mock<IRepositoryProvider> repo,
        Mock<IIssueProvider> issues,
        IReadOnlyList<PullRequestSummary> currentPrs,
        int limit = 5)
        => svc.ExecuteAsync(
            repo.Object, RepoId, issues.Object, IssueProviderId,
            currentPrs, wasInputTruncated: false, limit,
            branchCleanupEnabled: false, cleanupIntervalMinutes: 60,
            triggerCooldownMinutes: 25, CancellationToken.None);

    private (MeterListener Listener,
             ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> Counters,
             ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> Histograms)
        CreateMeterListener()
    {
        var counters = new ConcurrentBag<(string, long, KeyValuePair<string, object?>[])>();
        var histograms = new ConcurrentBag<(string, double, KeyValuePair<string, object?>[])>();

        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            // TODO: The comment below is misleading — no actual repo-ID tag filter is applied here.
            // Cross-test contamination is prevented by [Collection("Metrics")] serialization, not
            // by tag-based filtering. If the collection serialization is ever removed, unrelated
            // tests emitting to pipeline.pull_requests.closed could bleed into these assertions.
            // Consider adding a per-test discriminator tag or switching to isolated MeterProvider
            // instances to make the isolation explicit rather than relying on serialization.
            // Filter by repo ID to prevent cross-test contamination from parallel tests
            var tagsArr = tags.ToArray();
            if (instrument.Name == "pipeline.pull_requests.closed")
                counters.Add((instrument.Name, value, tagsArr));
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
        {
            if (instrument.Name == "pipeline.pull_requests.time_to_merge")
                histograms.Add((instrument.Name, value, tags.ToArray()));
        });
        listener.Start();
        return (listener, counters, histograms);
    }

    // ── Seed: PR in-flight, then disappears ───────────────────────────────────

    private static async Task SeedPrInFlight(HousekeepingService svc,
        Mock<IRepositoryProvider> providerMock, Mock<IIssueProvider> issueMock,
        PullRequestSummary pr)
    {
        // First cycle: PR is in the list → not evicted, added to in-flight
        // To get it into in-flight, we need the PR to be Behind first.
        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(pr.Number, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.Behind);
        providerMock.Setup(p => p.UpdatePullRequestBranchAsync(pr.Number, It.IsAny<CancellationToken>()))
                    .Returns(Task.CompletedTask);

        await ExecAsync(svc, providerMock, issueMock, [pr]);

        // Reset mergeability so subsequent cycles don't trigger updates
        providerMock.Setup(p => p.IsPullRequestBehindBaseAsync(pr.Number, It.IsAny<CancellationToken>()))
                    .ReturnsAsync(PrMergeabilityStatus.UpToDate);
    }

    // ── Counter: merged outcome ───────────────────────────────────────────────

    [Fact]
    public async Task WhenPrDisappearsFromList_EmitsMergedOutcomeOnce()
    {
        var (svc, repo, issues) = Create("merged");
        var pr = MakePr(101, DateTime.UtcNow.AddHours(-2));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);

        // Second cycle: PR absent → eviction + metric
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        var closed = counters.Where(c => c.Name == "pipeline.pull_requests.closed").ToList();
        closed.Should().ContainSingle("counter must fire exactly once");
        closed[0].Tags.Should().Contain(new KeyValuePair<string, object?>("outcome", "merged"));
        closed[0].Value.Should().Be(1L);
    }

    // ── Counter: closed_unmerged outcome ─────────────────────────────────────

    [Fact]
    public async Task WhenPrDisappearsFromList_EmitsClosedUnmergedOutcomeOnce()
    {
        var (svc, repo, issues) = Create("closed_unmerged");
        var pr = MakePr(102, DateTime.UtcNow.AddHours(-1));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        var closed = counters.Where(c => c.Name == "pipeline.pull_requests.closed").ToList();
        closed.Should().ContainSingle();
        closed[0].Tags.Should().Contain(new KeyValuePair<string, object?>("outcome", "closed_unmerged"));
    }

    // ── Deduplication: second poll doesn't re-emit ────────────────────────────

    [Fact]
    public async Task WhenSamePrDisappearsAcrossMultiplePollCycles_EmitsOnlyOnce()
    {
        var (svc, repo, issues) = Create("merged");
        var pr = MakePr(103, DateTime.UtcNow.AddHours(-3));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);

        // Second cycle: PR absent (metric should fire)
        await ExecAsync(svc, repo, issues, []);

        // Third cycle: PR still absent (metric should NOT fire again)
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        counters.Where(c => c.Name == "pipeline.pull_requests.closed").Should().ContainSingle(
            "counter must fire at most once per PR regardless of how many cycles it stays absent");
    }

    // ── Histogram: merged PR with CreatedAt ───────────────────────────────────

    [Fact]
    public async Task WhenMergedPr_EmitsTimeToMergeHistogram()
    {
        var createdAt = DateTime.UtcNow.AddHours(-5);
        var now = DateTimeOffset.UtcNow;
        var expectedSeconds = (now - createdAt).TotalSeconds;

        var (svc, repo, issues) = Create("merged");
        svc.UtcNow = () => now;

        // TODO: expectedSeconds is computed from DateTimeOffset.UtcNow captured *before* SeedPrInFlight
        // runs. The seed call involves async execution (mock setups, await ExecAsync) and real clock
        // time elapses before RecordPrOutcomesAsync is called with the fixed svc.UtcNow value. The
        // 1.0-second precision tolerance may be insufficient on a slow CI machine. Fix: compute
        // expectedSeconds from a fixed clock value that is also assigned to svc.UtcNow — both should
        // use the same DateTimeOffset constant so the expected and actual values are derived
        // identically regardless of wall-clock time elapsed during the seed step.

        var pr = MakePr(104, createdAt);
        var (listener, _, histograms) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        var timeToMerge = histograms.Where(h => h.Name == "pipeline.pull_requests.time_to_merge").ToList();
        timeToMerge.Should().ContainSingle("histogram must fire once for a merged PR");
        timeToMerge[0].Value.Should().BeApproximately(expectedSeconds, precision: 1.0,
            "histogram value must be (UtcNow - CreatedAt).TotalSeconds");
    }

    // ── Histogram: closed_unmerged PR does NOT emit histogram ────────────────

    [Fact]
    public async Task WhenClosedUnmergedPr_DoesNotEmitTimeToMergeHistogram()
    {
        var (svc, repo, issues) = Create("closed_unmerged");
        var pr = MakePr(105, DateTime.UtcNow.AddHours(-2));
        var (listener, _, histograms) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        histograms.Where(h => h.Name == "pipeline.pull_requests.time_to_merge").Should().BeEmpty(
            "histogram must NOT fire for closed_unmerged PRs");
    }

    // ── Histogram: CreatedAt null → no histogram ──────────────────────────────

    [Fact]
    public async Task WhenMergedPr_CreatedAtIsNull_TimeToMergeHistogramSkipped()
    {
        var (svc, repo, issues) = Create("merged");
        var pr = MakePr(106, createdAt: null); // no creation timestamp
        var (listener, _, histograms) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        histograms.Where(h => h.Name == "pipeline.pull_requests.time_to_merge").Should().BeEmpty(
            "histogram must not fire when CreatedAt is null");
    }

    // ── Null outcome: no metric ───────────────────────────────────────────────

    [Fact]
    public async Task WhenGetPrOutcomeReturnsNull_NeitherCounterNorHistogramEmitted()
    {
        // Default seam returns null
        var (svc, repo, issues) = Create();
        var pr = MakePr(107, DateTime.UtcNow.AddHours(-1));
        var (listener, counters, histograms) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        counters.Should().BeEmpty("no counter when outcome is null");
        histograms.Should().BeEmpty("no histogram when outcome is null");
    }

    // ── PR not in in-flight: no metric ───────────────────────────────────────

    [Fact]
    public async Task WhenPrWasNeverInFlight_AndDisappearsFromList_NoMetricEmitted()
    {
        // PR appears in the list but never gets into in-flight (e.g., it was UpToDate all along).
        // Then on second cycle it's gone. Since it was never in in-flight, no metric fires.
        // TODO: This test passes trivially because the PR is UpToDate throughout and therefore never
        // enters the inFlight HashSet. When RecordPrOutcomesAsync iterates inFlight it finds an empty
        // set, so the assertion holds regardless of whether the guard is the in-flight membership
        // check or the deduplication check. A future refactor that iterates currentPrNumbers instead
        // of inFlight would still pass this test, silently breaking the boundary. To make the
        // test meaningful, verify the mechanism explicitly: confirm that after two cycles inFlight is
        // empty, for example by re-introducing the PR and asserting that the counter still fires once
        // (not twice), proving the in-flight guard — not just set emptiness — prevents double-emission.
        var (svc, repo, issues) = Create("merged");
        var pr = MakePr(108, DateTime.UtcNow.AddHours(-1));
        var (listener, counters, _) = CreateMeterListener();

        // PR is UpToDate (not Behind), so it never enters in-flight
        // First cycle: PR present, UpToDate
        await ExecAsync(svc, repo, issues, [pr]);
        // Second cycle: PR absent — but was never in in-flight
        await ExecAsync(svc, repo, issues, []);
        listener.Dispose();

        counters.Should().BeEmpty(
            "counter must not fire for PRs that were never tracked in the in-flight set");
    }
}
