using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net.Http;
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
        staleMock.Setup(s => s.RunIfDueAsync(It.IsAny<StaleBranchCleanupRequest>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var reworkMock = new Mock<IIssueReworkService>();
        reworkMock.Setup(s => s.TriggerConflictReworkAsync(It.IsAny<ConflictReworkRequest>(), It.IsAny<CancellationToken>()))
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
            new HousekeepingRequest
            {
                RepoProvider = repo.Object,
                RepoProviderId = RepoId,
                IssueProvider = issues.Object,
                IssueProviderId = IssueProviderId,
                AgentDonePrs = currentPrs,
                WasInputTruncated = false,
                EffectiveConcurrencyLimit = limit,
                BranchCleanupEnabled = false,
                CleanupIntervalMinutes = 60,
                TriggerCooldownMinutes = 25
            },
            CancellationToken.None);

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

    // ── Transient exception: retry on later sweep ─────────────────────────────

    [Fact]
    public async Task WhenGetPrOutcomeThrowsTransientException_OnFirstSweep_MetricFiredOnLaterSweep()
    {
        // AC1: a test where GetPrOutcomeAsync throws on sweep 1 and returns a resolved outcome
        // on sweep 2 MUST fail against the OLD code (which called recordedForRepo.Add BEFORE the
        // seam) and MUST pass against the NEW code (which only calls Add after a successful return).
        //
        // Why the seam flag is required:
        //   EvictInFlightSlots (Step 3) runs AFTER RecordPrOutcomesAsync (Step 2.5) in the same
        //   ExecuteAsync call and unconditionally calls recordedForRepo.Remove for absent PRs.
        //   Without SkipRecordedOutcomesEvictionCleanup, Step 3 clears the entry that OLD code
        //   added prematurely, so both old and new code produce identical state after a full call —
        //   making the bug unobservable through the public API.
        //   With the seam enabled, the Step-3 cleanup is bypassed for absent PRs, so the premature
        //   Add by OLD code persists into the next sweep: the dedup Contains guard fires, the seam
        //   is never called on sweep 2, and the metric is permanently suppressed — exactly the
        //   bug the issue describes.
        //
        // Structure: seed → sweep 1 (absent, throw) → sweep 2 (absent, succeed).
        // OLD code: sweep 1 adds to recordedForRepo, seam throws, seam not cleaned up → sweep 2
        //           hits Contains guard → seam never called (callCount=1), metric never fires.
        // NEW code: sweep 1 does NOT add (catch does not add) → sweep 2 reaches seam (callCount=2)
        //           → metric fires once.
        //
        // NOTE: between sweep 1 and sweep 2 the PR must still be in inFlight. Because Step 3's
        // absent-path also removes from inFlight (not gated by the seam flag), we re-seed the PR
        // between the two sweeps. Re-seeding produces identical clean inFlight state for old and
        // new code because the test scenario relies on recordedForRepo, not inFlight, to
        // distinguish them.
        // TODO: callCount is captured by a lambda without volatile or Interlocked access. This is
        // safe today because ExecuteAsync is called synchronously-sequentially (no thread pool
        // dispatch for the seam), but the assertion at the end could read a stale value if the seam
        // were ever invoked off-thread. Consider using Interlocked.Increment to be explicit.
        // (DotNetSpecialist review finding.)
        var callCount = 0;
        var (svc, repo, issues) = Create(outcomeSeam: (_, _, _) =>
        {
            callCount++;
            if (callCount == 1)
                throw new HttpRequestException("transient 503");
            return Task.FromResult<string?>("merged");
        });
        // Enable the test seam so Step 3 does NOT clear recordedForRepo for absent PRs.
        // This makes the premature recordedForRepo.Add in OLD code observable across sweeps.
        svc.SkipRecordedOutcomesEvictionCleanup = true;

        var pr = MakePr(109, DateTime.UtcNow.AddHours(-2));
        var (listener, counters, _) = CreateMeterListener();

        // Seed: PR enters inFlight.
        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 1 (PR absent): seam is called, throws.
        // OLD code: recordedForRepo.Add(109) fires BEFORE the throw — entry persists (seam not cleared).
        // NEW code: catch block does NOT add — recordedForRepo stays empty.
        await ExecAsync(svc, repo, issues, []);

        callCount.Should().Be(1, "seam must be called exactly once on sweep 1");
        counters.Should().BeEmpty("no metric must fire when the seam throws");

        // Re-seed: PR re-enters inFlight (Step 3 removed it from inFlight on sweep 1's absent path).
        // TODO: this re-seed relies on Step 3's absent-path eviction having removed the PR from
        // inFlight during sweep 1 (the PR was absent, so EvictInFlightSlots.Remove fired).
        // If Step 3's eviction conditions change (e.g. Unknown status exempt from absent-eviction),
        // re-seeding may add a no-op and the discriminating assertion could still pass. Consider
        // adding an explicit assertion that inFlight is empty before re-seeding, or exposing an
        // inFlight-inspection seam. (DotNetSpecialist review finding.)
        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 2 (PR absent): check whether recordedForRepo contains #109.
        // OLD code: Contains(109) → true → skip entire body → seam never reached (callCount stays 1, no metric).
        // NEW code: Contains(109) → false → seam called → returns "merged" (callCount=2) → metric fires.
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        var closed = counters.Where(c => c.Name == "pipeline.pull_requests.closed").ToList();
        closed.Should().ContainSingle(
            "metric must fire exactly once on sweep 2 — a transient exception on sweep 1 must not " +
            "permanently suppress the counter (OLD code fails: callCount stays 1 and no metric fires)");
        closed[0].Tags.Should().Contain(new KeyValuePair<string, object?>("outcome", "merged"));
        callCount.Should().Be(2,
            "seam must be invoked again on sweep 2 — with OLD code the premature recordedForRepo.Add " +
            "prevents the seam from being reached, so callCount stays 1");
    }

    // ── Transient exception: PR not deduped after throw ──────────────────────

    [Fact]
    public async Task WhenGetPrOutcomeThrowsTransientException_PrNotRecordedInDedup()
    {
        // AC2: after a sweep where the outcome seam throws, prNumber must NOT be in
        // recordedForRepo. Verified by confirming the seam is called again on a subsequent sweep.
        //
        // Uses SkipRecordedOutcomesEvictionCleanup so Step 3 does NOT clear recordedForRepo
        // for absent PRs. Without the seam, both old and new code produce identical empty
        // recordedForRepo state after each sweep (Step 3 clears whatever Step 2.5 added),
        // so the test would pass vacuously on both implementations.
        //
        // OLD code: sweep 1 adds to recordedForRepo before throw → seam cleanup skipped →
        //           entry persists → sweep 2 hits Contains guard → seam never called (callCount=1).
        // NEW code: sweep 1 catch block does NOT add → recordedForRepo empty → sweep 2 calls seam
        //           (callCount=2). Assertion callCount==2 fails on OLD code, passes on NEW.
        var callCount = 0;
        var (svc, repo, issues) = Create(outcomeSeam: (_, _, _) =>
        {
            callCount++;
            throw new HttpRequestException("transient 503");
        });
        svc.SkipRecordedOutcomesEvictionCleanup = true;

        var pr = MakePr(110, DateTime.UtcNow.AddHours(-1));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 1 (PR absent): seam throws.
        // OLD code: recordedForRepo.Add(110) runs before throw, seam cleanup skipped → entry persists.
        // NEW code: catch does not add → recordedForRepo empty.
        await ExecAsync(svc, repo, issues, []);

        counters.Should().BeEmpty("no metric must fire when the seam throws");
        callCount.Should().Be(1, "seam must be called on sweep 1");

        // Re-seed: PR re-enters inFlight.
        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 2 (PR absent): seam throws again.
        // OLD code: Contains(110) → true → skip → seam NOT reached (callCount stays 1).
        // NEW code: Contains(110) → false → seam called → throws (callCount=2).
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        counters.Should().BeEmpty("no metric must fire when the seam always throws");

        // This is the discriminating assertion: callCount==2 proves #110 was NOT in recordedForRepo
        // after sweep 1's exception. With OLD code, callCount stays 1 because the premature Add
        // caused the dedup guard to fire on sweep 2, permanently suppressing the retry.
        callCount.Should().Be(2,
            "seam must be invoked on sweep 2 — a count of 1 means the premature recordedForRepo.Add " +
            "(OLD code) persisted across the sweep boundary and blocked the retry (AC2 regression)");
    }

    // ── Successful outcome: dedup prevents re-emission, seam called only once ─

    [Fact]
    public async Task WhenGetPrOutcomeSucceeds_DeduplicationPreventsReEmissionOnSubsequentSweeps()
    {
        // AC3a: a successfully resolved outcome must be recorded in recordedForRepo so that
        // subsequent sweeps skip the seam and do not re-emit the metric.
        //
        // Uses SkipRecordedOutcomesEvictionCleanup so the dedup entry added in sweep 1 is NOT
        // cleared by Step 3's absent-path. Without the seam, sweep 2's inFlight is empty (Step 3
        // evicts the PR from inFlight on sweep 1), so the loop body is never entered and callCount
        // stays 1 regardless of whether recordedForRepo contains the entry — the test would pass
        // vacuously on both old and new code, and even on code where recordedForRepo.Add was
        // removed entirely.
        //
        // With the seam enabled and after re-seeding (so the PR re-enters inFlight for sweep 2):
        // NEW code: sweep 1 adds to recordedForRepo → sweep 2 Contains(prNumber) → true → skip
        //           → seam not called (callCount=1), metric fires exactly once. ✓
        // Broken code (recordedForRepo.Add removed): sweep 1 does not add → sweep 2 Contains →
        //           false → seam called (callCount=2), metric fires twice. Assertion fails. ✓
        var callCount = 0;
        var (svc, repo, issues) = Create(outcomeSeam: (_, _, _) =>
        {
            callCount++;
            return Task.FromResult<string?>("merged");
        });
        svc.SkipRecordedOutcomesEvictionCleanup = true;

        var pr = MakePr(111, DateTime.UtcNow.AddHours(-2));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 1 (PR absent): seam returns "merged" → recordedForRepo.Add(111) → metric fires.
        // With seam flag: Step 3 does NOT remove the entry from recordedForRepo.
        await ExecAsync(svc, repo, issues, []);

        // Re-seed: PR re-enters inFlight (Step 3 removed it from inFlight on sweep 1's absent-path).
        // recordedForRepo still contains #111 (seam flag prevents Step 3 from clearing it).
        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 2 (PR absent): Contains(111) → true → entire body skipped.
        // Seam must NOT be called (callCount stays 1), metric must NOT fire again.
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        counters.Where(c => c.Name == "pipeline.pull_requests.closed").Should().ContainSingle(
            "metric must fire exactly once — dedup must prevent re-emission on sweep 2 " +
            "(broken code without recordedForRepo.Add would call seam twice and fire metric twice)");

        callCount.Should().Be(1,
            "seam must be called exactly once — callCount==2 means recordedForRepo.Add was not " +
            "executed after GetPrOutcomeAsync returned, so dedup does not fire on sweep 2 (AC3 regression)");
    }

    // ── Null outcome: deduped after first sweep — seam not called again ───────

    [Fact]
    public async Task WhenGetPrOutcomeReturnsNull_DeduplicationPreventsRePollOnSubsequentSweeps()
    {
        // AC3b: a null outcome must also be deduped (recordedForRepo.Add fires BEFORE the null
        // check) so that the PR is not re-polled on every sweep indefinitely.
        //
        // Uses SkipRecordedOutcomesEvictionCleanup for the same reason as the test above:
        // without it, Step 3 clears recordedForRepo on sweep 1's absent-path and inFlight is
        // empty for sweep 2 — both old and new code produce callCount=1 for unrelated reasons,
        // making the test vacuous.
        //
        // NEW code: recordedForRepo.Add fires BEFORE the null check → sweep 2 Contains → true
        //           → seam not called (callCount=1). ✓
        // Broken placement (Add AFTER null check): null path hits `continue` before Add →
        //           sweep 2 Contains → false → seam called (callCount=2). Assertion fails. ✓
        var callCount = 0;
        var (svc, repo, issues) = Create(outcomeSeam: (_, _, _) =>
        {
            callCount++;
            return Task.FromResult<string?>(null);
        });
        svc.SkipRecordedOutcomesEvictionCleanup = true;

        var pr = MakePr(112, DateTime.UtcNow.AddHours(-1));
        var (listener, counters, _) = CreateMeterListener();

        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 1 (PR absent): seam returns null.
        // NEW code: recordedForRepo.Add(112) fires (placement: before null check) → no metric.
        // With seam flag: Step 3 does NOT remove the entry from recordedForRepo.
        await ExecAsync(svc, repo, issues, []);

        // Re-seed: PR re-enters inFlight.
        await SeedPrInFlight(svc, repo, issues, pr);

        // Sweep 2 (PR absent): Contains(112) → true → skip (seam not called).
        // Broken placement: Contains(112) → false → seam called (callCount=2) → null again.
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        counters.Should().BeEmpty("no metric must fire when the outcome seam returns null");

        callCount.Should().Be(1,
            "seam must be called exactly once — callCount==2 means recordedForRepo.Add is placed " +
            "AFTER the null check, so null-outcome PRs are not deduped and would be re-polled " +
            "on every subsequent sweep indefinitely (AC3b regression)");
    }

    // ── Dictionary eviction: both shrink when PR leaves in-flight ─────────────

    [Fact]
    public async Task WhenPrLeavesInFlightSet_BothDictionariesShrink()
    {
        // Observable proxy: _recordedPrOutcomes deduplication prevents re-emission.
        // If the inner HashSet entry was correctly removed during eviction, a re-seeded
        // PR can have its counter metric fire again. Similarly, if _prCreatedAtCache was
        // cleared, re-seeding with a new CreatedAt allows the histogram to re-fire.
        var firstCreatedAt = DateTime.UtcNow.AddHours(-4);
        var secondCreatedAt = DateTime.UtcNow.AddHours(-1);
        var fixedNow = DateTimeOffset.UtcNow;

        var (svc, repo, issues) = Create("merged");
        svc.UtcNow = () => fixedNow;

        var pr = MakePr(200, firstCreatedAt);
        var (listener, counters, histograms) = CreateMeterListener();

        // ── Cycle 1 (seed): PR is Behind → enters inFlight ───────────────────
        await SeedPrInFlight(svc, repo, issues, pr);

        // ── Cycle 2 (eviction): PR absent from list ───────────────────────────
        // RecordPrOutcomesAsync (Step 2.5) fires the metric and adds the PR to
        // recordedForRepo. EvictInFlightSlots (Step 3) then removes the PR from
        // inFlight, _lastTriggeredAt, _prCreatedAtCache, and recordedForRepo —
        // all within the same ExecuteAsync call.
        await ExecAsync(svc, repo, issues, []);

        // Positive-count assertion before the eviction check — prevents a vacuous pass.
        var closedAfterFirstEviction = counters.Where(c => c.Name == "pipeline.pull_requests.closed").ToList();
        closedAfterFirstEviction.Should().ContainSingle(
            "counter must fire once when PR first disappears from the agent:done list");

        // ── Cycle 3 (re-seed): re-introduce PR as Behind ─────────────────────
        // inFlight slot is now free (cleared in cycle 2), so the PR can re-enter.
        // _prCreatedAtCache is repopulated with the new CreatedAt.
        var prReseeded = MakePr(200, secondCreatedAt);
        repo.Setup(p => p.IsPullRequestBehindBaseAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.Behind);
        repo.Setup(p => p.UpdatePullRequestBranchAsync(200, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        await ExecAsync(svc, repo, issues, [prReseeded]);

        // Reset mergeability for the second eviction cycle
        repo.Setup(p => p.IsPullRequestBehindBaseAsync(200, It.IsAny<CancellationToken>()))
            .ReturnsAsync(PrMergeabilityStatus.UpToDate);

        // ── Cycle 4 (second eviction): PR absent again ────────────────────────
        // If _recordedPrOutcomes was NOT cleared in cycle 2, recordedForRepo.Add(200)
        // returns false and the counter does NOT fire → test fails. This is the proof
        // that the inner HashSet entry was evicted.
        // If _prCreatedAtCache was NOT cleared in cycle 2, the histogram would still
        // use firstCreatedAt instead of secondCreatedAt → histogram value would differ.
        await ExecAsync(svc, repo, issues, []);

        listener.Dispose();

        var allClosed = counters.Where(c => c.Name == "pipeline.pull_requests.closed").ToList();
        allClosed.Should().HaveCount(2,
            "counter must fire twice (once per eviction cycle) — proving _recordedPrOutcomes entry was cleared after the first eviction");

        var allHistograms = histograms.Where(h => h.Name == "pipeline.pull_requests.time_to_merge").ToList();
        allHistograms.Should().HaveCount(2,
            "histogram must fire twice (once per eviction cycle) — proving _prCreatedAtCache entry was cleared after the first eviction");

        // The histogram values must include one based on firstCreatedAt and one on secondCreatedAt —
        // proving the cache was re-populated with the new value rather than retaining the old one.
        // ConcurrentBag has no guaranteed order, so assert on the set of values rather than indices.
        // TODO: The discriminability of the histogram value assertions relies on firstCreatedAt and
        // secondCreatedAt being far enough apart (currently ~3 h) to exceed the ±2 s tolerance.
        // If these constants are ever adjusted to be close together (within a few seconds of each
        // other), a broken _prCreatedAtCache eviction could pass undetected. Consider enforcing a
        // minimum gap assertion (e.g. Assert.True(secondCreatedAt - firstCreatedAt > TimeSpan.FromMinutes(1)))
        // or switching to a clock-injected approach that makes the gap explicit and invariant.
        var expectedSecondsFirst = (fixedNow - firstCreatedAt).TotalSeconds;
        var expectedSecondsSecond = (fixedNow - secondCreatedAt).TotalSeconds;
        var histogramValues = allHistograms.Select(h => h.Value).ToList();
        histogramValues.Should().ContainSingle(v => Math.Abs(v - expectedSecondsFirst) <= 2.0,
            "one histogram emission must use firstCreatedAt (~4 h)");
        histogramValues.Should().ContainSingle(v => Math.Abs(v - expectedSecondsSecond) <= 2.0,
            "one histogram emission must use secondCreatedAt (~1 h), proving _prCreatedAtCache was evicted and repopulated");
    }
}
