using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.Extensions.Hosting;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the new API-side metrics introduced in issue #2974:
/// <list type="bullet">
///   <item><c>pipeline.run.step.duration</c> — recorded per step transition</item>
///   <item><c>pipeline.run.sub_issues</c> — recorded once per decomposition run</item>
///   <item><c>pipeline.run.brain_updates</c> — recorded once per non-consolidation run completion</item>
/// </list>
///
/// AC: "A run that walks N steps produces N−1 samples (unit test with a fake clock)."
/// Note on "fake clock": HandleStepTransition uses DateTimeOffset.UtcNow directly (no injected clock).
/// Tests satisfy the AC by injecting controlled past-timestamps that deterministically pass the
/// clamping check (timestamp &lt;= DateTimeOffset.UtcNow), producing exact, predictable deltas.
/// </summary>
[Collection("Metrics")]
public sealed class PipelineRunStepAndRunMetricsTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<IRunLifecycleManager> _lifecycle = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IHubIssueOperations> _issueOps = new();
    private readonly Mock<IChangeNotifier> _changeNotifier = new();
    private readonly Mock<IHostApplicationLifetime> _appLifetime = new();
    private readonly Mock<IFeedbackCommentOutbox> _outbox = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentJobLifecycleService _sut;

    public PipelineRunStepAndRunMetricsTests()
    {
        _appLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        _sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                _lifecycle.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private PipelineRun MakeRun(string jobId = "job-1", PipelineRunType runType = PipelineRunType.Implementation)
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = jobId,
            IssueIdentifier = "owner/repo#42",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });
        return run;
    }

    private void SetupFacadeRun(PipelineRun run)
    {
        var jobId = new JobId(run.RunId);
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(run));
        _facade.Setup(f => f.TouchLastProgressAsync(jobId, It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private static (MeterListener Listener, ConcurrentBag<(double Value, string RunType, string Step)> Bag)
        SetupStepDurationListener()
    {
        var bag = new ConcurrentBag<(double, string, string)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.step.duration")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            string runType = "", step = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "run_type") runType = tag.Value?.ToString() ?? "";
                else if (tag.Key == "step") step = tag.Value?.ToString() ?? "";
            }
            bag.Add((measurement, runType, step));
        });
        listener.Start();
        return (listener, bag);
    }

    private static (MeterListener Listener, ConcurrentBag<(long Value, string Result)> Bag)
        SetupSubIssuesListener()
    {
        var bag = new ConcurrentBag<(long, string)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.sub_issues")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return; // skip pre-init
            string result = "";
            foreach (var tag in tags)
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            bag.Add((measurement, result));
        });
        listener.Start();
        return (listener, bag);
    }

    private static (MeterListener Listener, ConcurrentBag<(long Value, string Result)> Bag)
        SetupBrainUpdatesListener()
    {
        var bag = new ConcurrentBag<(long, string)>();
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName
                && instrument.Name == "pipeline.run.brain_updates")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement == 0) return; // skip pre-init
            string result = "";
            foreach (var tag in tags)
                if (tag.Key == "result") result = tag.Value?.ToString() ?? "";
            bag.Add((measurement, result));
        });
        listener.Start();
        return (listener, bag);
    }

    // ── Step Duration: N steps → N−1 samples ──────────────────────────────────

    /// <summary>
    /// AC: "A run that walks N steps produces N−1 samples."
    /// Walk 4 steps; verify exactly 3 histogram samples, one per completed step.
    /// Timestamps are injected as past values so the clamping check always passes.
    /// The run's LastStepChangeAt is set to a known past baseline to produce positive deltas.
    /// </summary>
    [Fact]
    public void HandleStepTransition_FourStepsWalked_ProducesThreeSamples()
    {
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        // Set a known baseline for LastStepChangeAt so the first delta is positive.
        var baseline = DateTimeOffset.UtcNow.AddSeconds(-120);
        run.LastStepChangeAt = baseline;

        var (listener, bag) = SetupStepDurationListener();
        try
        {
            var t1 = baseline.AddSeconds(30); // +30s from baseline
            var t2 = t1.AddSeconds(30);       // +30s from t1
            var t3 = t2.AddSeconds(30);       // +30s from t2

            // All timestamps are in the past, so the clamping check will pass.
            // Transition 1: Created → CloningRepository (records duration of Created = ~30s)
            _sut.HandleStepTransition(jobId, PipelineStep.CloningRepository, t1, null);
            // Transition 2: CloningRepository → AnalyzingCode (records duration of CloningRepository = 30s)
            _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, t2, null);
            // Transition 3: AnalyzingCode → GeneratingCode (records duration of AnalyzingCode = 30s)
            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, t3, null);

            bag.Should().HaveCount(3,
                "3 step transitions produce 3 samples (one per completed step)");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_RecordsCorrectStepTag_ForCompletedStep()
    {
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        // Set known baseline for first positive delta
        var baseline = DateTimeOffset.UtcNow.AddSeconds(-120);
        run.LastStepChangeAt = baseline;

        var (listener, bag) = SetupStepDurationListener();
        try
        {
            var t1 = baseline.AddSeconds(30);
            var t2 = t1.AddSeconds(30);

            // run starts at Created; transition to CloningRepository records duration of 'Created' step
            _sut.HandleStepTransition(jobId, PipelineStep.CloningRepository, t1, null);
            // transition to AnalyzingCode records duration of 'CloningRepository' step
            _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, t2, null);

            var steps = bag.Select(s => s.Step).ToList();
            steps.Should().Contain(PipelineStep.Created.ToString(),
                "first transition records the duration of 'Created' step");
            steps.Should().Contain(PipelineStep.CloningRepository.ToString(),
                "second transition records the duration of 'CloningRepository' step");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_RecordsCorrectRunTypeTag()
    {
        var run = MakeRun(runType: PipelineRunType.Implementation);
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        // Set known baseline before the injected timestamp
        var baseline = DateTimeOffset.UtcNow.AddSeconds(-60);
        run.LastStepChangeAt = baseline;

        var (listener, bag) = SetupStepDurationListener();
        try
        {
            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, baseline.AddSeconds(30), null);

            bag.Should().ContainSingle(s => s.RunType == "implementation",
                "run_type tag must be the lowercase PipelineRunType name");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_SameStepSentTwice_OnlyOneAdditionalSample()
    {
        // No-op transitions (same step) should NOT produce a new sample.
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        var baseline = DateTimeOffset.UtcNow.AddSeconds(-120);
        run.LastStepChangeAt = baseline;

        var (listener, bag) = SetupStepDurationListener();
        try
        {
            // First: Created → GeneratingCode (1 sample)
            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, baseline.AddSeconds(30), null);
            var after_first = bag.Count;

            // Second: GeneratingCode → GeneratingCode (no-op, should be 0 additional samples)
            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, baseline.AddSeconds(45), null);
            var after_noop = bag.Count;

            after_noop.Should().Be(after_first,
                "no-op transition (same step) must not produce a new sample");

            // Third: GeneratingCode → RunningQualityGates (1 sample for GeneratingCode duration)
            _sut.HandleStepTransition(jobId, PipelineStep.RunningQualityGates, baseline.AddSeconds(60), null);

            bag.Should().HaveCount(after_first + 1,
                "resuming with a different step resumes recording");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_DeterministicDelta_MatchesInjectedTimestamps()
    {
        // Verify that the recorded duration equals exactly the difference between injected timestamps.
        // AC #3: "A test asserts step-duration telemetry is recorded with the correct delta across
        // a step transition." Also guards the ordering invariant: previousStepChangedAt returned by
        // ApplyStepMutation must be the old LastStepChangeAt, not the clamped timestamp.
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        // Set known baseline for first positive delta
        var baseline = DateTimeOffset.UtcNow.AddSeconds(-200);
        run.LastStepChangeAt = baseline;

        var (listener, bag) = SetupStepDurationListener();
        try
        {
            var t1 = baseline.AddSeconds(30);  // Created → CloningRepository: 30s for Created
            var t2 = t1.AddSeconds(45);         // CloningRepository → AnalyzingCode: 45s for CloningRepository

            // First transition sets LastStepChangeAt = t1; records Created duration = 30s
            _sut.HandleStepTransition(jobId, PipelineStep.CloningRepository, t1, null);

            // Second transition: should record the duration of CloningRepository = exactly 45s.
            // Filter by step name rather than clearing the bag — bag.Clear() on a ConcurrentBag
            // races with the MeterListener callback and can cause spurious failures.
            _sut.HandleStepTransition(jobId, PipelineStep.AnalyzingCode, t2, null);

            bag.Should().ContainSingle(
                s => s.Step == PipelineStep.CloningRepository.ToString()
                     && Math.Abs(s.Value - 45.0) < 1.0,
                // TODO: [WARNING] Tolerance < 1.0 second is too wide for a test that injects exact
                // timestamps and makes no system calls between transitions — the delta is exactly 45.0s
                // and any deviation indicates a real bug. Tighten to < 0.001 to make the test a precise
                // guard of the ordering invariant. Also, if more transitions are added in future,
                // ContainSingle (predicate form) will spuriously fail if two elements match.
                // (TestQualityReviewer L328)
                "the recorded duration must equal the difference between the two injected timestamps (≈45s)");
        }
        finally
        {
            listener.Dispose();
        }
    }

    // ── Sub-Issue Counter: once per run ───────────────────────────────────────

    [Fact]
    public void HandleStepTransition_WithSubIssueMetadata_RecordsCreatedAndFailed()
    {
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        var (listener, bag) = SetupSubIssuesListener();
        try
        {
            // Simulate metadata arriving with sub-issue counts
            // TODO: [WARNING] Metadata keys are hard-coded strings here. If StepMetadataApplier.Apply
            // is ever refactored to use different key names, this test will silently stop applying
            // metadata (Apply will parse 0 for both fields) and the assertion will fail as a false
            // negative. If these keys are defined as constants in production code, reference them
            // symbolically here to create a compile-time binding. See review findings [WARNING]
            // TestQualityReviewer L241.
            var metadata = new Dictionary<string, string>
            {
                ["DecompositionSubIssuesCreated"] = "5",
                ["DecompositionSubIssuesAttempted"] = "7"
            };

            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow.AddSeconds(-5), metadata);

            // 5 created, 2 failed (7 - 5 = 2)
            bag.Should().Contain(s => s.Result == "created" && s.Value == 5,
                "should record 5 sub-issues created");
            bag.Should().Contain(s => s.Result == "failed" && s.Value == 2,
                "should record 2 sub-issues failed (7 attempted - 5 created)");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_WithSubIssueMetadata_OncePerRunGuard()
    {
        // Sending the same metadata a second time must NOT produce additional measurements.
        // TODO: [WARNING] This test does not cover the case where metadata is retransmitted with
        // *incremented* counts (e.g. DecompositionSubIssuesAttempted goes from 3 to 5 on a retry).
        // The guard `previousSubIssuesAttempted == 0` still prevents re-emission in that case
        // (attempted is already non-zero), but this boundary condition is untested. Add a test that
        // sends metadata with Attempted=3 first, then Attempted=5, and asserts no second emission.
        // See review findings [WARNING] TestQualityReviewer L265.
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        var (listener, bag) = SetupSubIssuesListener();
        try
        {
            var metadata = new Dictionary<string, string>
            {
                ["DecompositionSubIssuesCreated"] = "3",
                ["DecompositionSubIssuesAttempted"] = "3"
            };

            // First call — should record
            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow.AddSeconds(-10), metadata);
            var after_first = bag.Count;

            // Second call with same metadata — attempted is now > 0 already, so guard should fire
            _sut.HandleStepTransition(jobId, PipelineStep.RunningQualityGates, DateTimeOffset.UtcNow.AddSeconds(-5), metadata);

            bag.Should().HaveCount(after_first,
                "second call with same metadata must not produce additional measurements (once-per-run guard)");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public void HandleStepTransition_WithAllSubIssuesCreated_OnlyRecordsCreatedNotFailed()
    {
        var run = MakeRun();
        var jobId = new JobId(run.RunId);
        SetupFacadeRun(run);

        var (listener, bag) = SetupSubIssuesListener();
        try
        {
            var metadata = new Dictionary<string, string>
            {
                ["DecompositionSubIssuesCreated"] = "4",
                ["DecompositionSubIssuesAttempted"] = "4"
            };

            _sut.HandleStepTransition(jobId, PipelineStep.GeneratingCode, DateTimeOffset.UtcNow.AddSeconds(-5), metadata);

            bag.Should().Contain(s => s.Result == "created" && s.Value == 4,
                "all 4 created");
            bag.Should().NotContain(s => s.Result == "failed",
                "no failed when all sub-issues succeeded");
        }
        finally
        {
            listener.Dispose();
        }
    }

    // ── Brain Update Counter: once per non-consolidation run ──────────────────

    /// <remarks>
    /// Brain counter is recorded in HandleJobCompletedAsync inside the consolidation guard.
    /// We test it by verifying the in-memory counter on a mock payload that sets BrainUpdatesPushed.
    /// </remarks>
    [Fact]
    public async Task HandleJobCompletedAsync_WithBrainPushed_RecordsPushedResult()
    {
        var run = MakeRun();
        run.BrainUpdatesPushed = false; // will be set by JobCompletionMapper.Apply
        var jobId = new JobId(run.RunId);

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(run));

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            // TODO: [WARNING] CompleteRunAsync returns null to simulate the "race — already terminated"
            // path. The brain counter is recorded on the pre-captured outer `run` reference, which is
            // mutated by JobCompletionMapper.Apply inside strategy.ExecuteAsync. If the production code
            // changed to read BrainUpdatesPushed from the returned value instead of the pre-captured
            // reference, this test would silently stop covering the correct code path while still
            // passing. Consider asserting that run.BrainUpdatesPushed was actually set to true by
            // Apply before the metric fires. See review findings [WARNING] TestQualityReviewer L349.
            .ReturnsAsync((PipelineRun?)null); // simulate race — no bookkeeping path needed

        // Use a sut that uses our lifecycle mock
        var sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                lifecycleManager.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));

        var (listener, bag) = SetupBrainUpdatesListener();
        try
        {
            var payload = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                BrainUpdatesPushed = true,
                FinalLabel = "agent:done"
            };

            // Need to set up facade.TransitionWorkItemAsync for the race path
            _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
                .ReturnsAsync(true);

            await sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

            bag.Should().Contain(s => s.Result == "pushed",
                "brain counter must record 'pushed' when BrainUpdatesPushed=true");
            bag.Should().NotContain(s => s.Result == "none",
                "must not also record 'none'");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task HandleJobCompletedAsync_WithBrainNotPushed_RecordsNoneResult()
    {
        var run = MakeRun();
        run.BrainUpdatesPushed = false;
        var jobId = new JobId(run.RunId);

        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(run));
        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                lifecycleManager.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));

        var (listener, bag) = SetupBrainUpdatesListener();
        try
        {
            var payload = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                BrainUpdatesPushed = false,
                FinalLabel = "agent:done"
            };

            await sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

            bag.Should().Contain(s => s.Result == "none",
                "brain counter must record 'none' when BrainUpdatesPushed=false");
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Fact]
    public async Task HandleJobCompletedAsync_ConsolidationRun_DoesNotRecordBrainCounter()
    {
        // Consolidation runs must NOT emit the brain counter.
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "consolidation-job",
            IssueIdentifier = "consolidation",
            IssueTitle = "Consolidation",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId, // consolidation sentinel
            RepoProviderConfigId = "rp-1",
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

        var jobId = new JobId(run.RunId);
        _facade.Setup(f => f.GetRun(jobId)).Returns(run);
        _facade.Setup(f => f.ReplaceRun(run));

        var lifecycleManager = new Mock<IRunLifecycleManager>();
        lifecycleManager
            .Setup(m => m.CompleteRunAsync(It.IsAny<RunId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync((PipelineRun?)null);

        var sut = new AgentJobLifecycleService(
            new AgentJobLifecycleServiceDependencies(
                _facade.Object,
                lifecycleManager.Object,
                _labelService.Object,
                _issueOps.Object,
                _changeNotifier.Object,
                _appLifetime.Object,
                _outbox.Object,
                _logger.Object));

        _facade.Setup(f => f.TransitionWorkItemAsync(It.IsAny<JobId>(), It.IsAny<WorkItemStatus>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<FailureReason?>()))
            .ReturnsAsync(true);

        var (listener, bag) = SetupBrainUpdatesListener();
        try
        {
            var payload = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                CompletedAt = DateTimeOffset.UtcNow,
                BrainUpdatesPushed = true
            };

            await sut.HandleJobCompletedAsync(jobId, agent: null, payload, CancellationToken.None);

            bag.Should().BeEmpty(
                "consolidation runs must not emit the brain_updates counter");
        }
        finally
        {
            listener.Dispose();
        }
    }
}
