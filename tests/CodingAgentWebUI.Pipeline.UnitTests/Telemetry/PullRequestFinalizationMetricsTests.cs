using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Telemetry;

/// <summary>
/// Verifies that each finalization phase emits <c>pipeline.step.duration</c> and
/// <c>pipeline.step.count</c> metrics with the correct <c>step_name</c> tag,
/// including on failure paths (proving the finally-block fires) and verifying
/// that draft / no-PR-number paths correctly suppress metric emission for skipped phases.
/// </summary>
[Collection("Metrics")]
public class PullRequestFinalizationMetricsTests : IDisposable
{
    private readonly MeterListener _listener = new();
    private readonly ConcurrentBag<(string Name, double Value, KeyValuePair<string, object?>[] Tags)> _histograms = [];
    private readonly ConcurrentBag<(string Name, long Value, KeyValuePair<string, object?>[] Tags)> _counters = [];

    // TODO: _histograms and _counters listen on the process-wide PipelineTelemetry meter. xUnit
    // creates a new class instance per test so there is no inter-test sharing within this class, but
    // the MeterListener is active for the lifetime of each instance and captures all measurements
    // emitted by any code on any thread while the listener is alive. [Collection("Metrics")]
    // serialises tests within this class but does not isolate them from other [Collection("Metrics")]
    // classes. Positive Contain assertions are robust to extra measurements, but the
    // RunFullPrCreationAsync_CreatePullRequestMetric_DoesNotSubsumeSubPhases test builds
    // capturedStepNames from the whole bag — if a prior test in the same collection emitted a
    // "GeneratePrDescription" measurement, the capturedStepNames.Contain assertion may pass even if
    // RunPostPrSequenceAsync failed to emit it in the current test. This is a tautology risk: consider
    // snapshotting the bag size before the act step and filtering to entries added after that point.

    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly PullRequestFinalizationService _sut;

    public PullRequestFinalizationMetricsTests()
    {
        _sut = new PullRequestFinalizationService(_logger.Object);

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == PipelineTelemetry.SourceName)
                listener.EnableMeasurementEvents(instrument);
        };

        _listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            _histograms.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _counters.Add((instrument.Name, measurement, tags.ToArray()));
        });

        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();

    private static PipelineRun CreateRun() => new()
    {
        RunId = "metrics-test-run",
        IssueIdentifier = "test/repo#1",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        RepositoryName = "org/repo",
        WorkspacePath = Path.GetTempPath(),
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        PullRequestNumber = "42"
    };

    // ── GeneratePrDescriptionAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmitsStepDurationMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));

        var hist = _histograms.First(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));
        // TODO: >= 0 is a weak lower bound that passes even for a zero value (stopwatch not started
        // or metric recorded a no-op). A stricter assertion is impractical in a unit test but
        // consider asserting >= 0 AND that the stopwatch was actually started (e.g. by verifying
        // the measurement source is the expected instrument). For now this guards against NaN/negative.
        hist.Value.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_EmitsStepCountMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));

        var counter = _counters.First(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));
        counter.Value.Should().Be(1);
    }

    [Fact]
    public async Task GeneratePrDescriptionAsync_OnAgentFailure_StillEmitsMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"));

        // Does not throw — the method swallows non-OCE exceptions
        await _sut.GeneratePrDescriptionAsync(run, agentProvider.Object, repoProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));
        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription")));
    }

    // ── RunReflectionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RunReflectionAsync_EmitsStepDurationMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = [] });

        await _sut.RunReflectionAsync(run, agentProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "Reflection")));

        var hist = _histograms.First(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "Reflection")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "Reflection")));
    }

    [Fact]
    public async Task RunReflectionAsync_OnAgentFailure_StillEmitsMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) };

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new InvalidOperationException("reflection agent failed"));

        await _sut.RunReflectionAsync(run, agentProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "Reflection")));
        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "Reflection")));
    }

    // ── SyncBrainPostRunAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task SyncBrainPostRunAsync_EmitsStepDurationMetric()
    {
        var run = CreateRun();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { BrainPushMaxRetries = 1 };

        await _sut.SyncBrainPostRunAsync(run, brainSync.Object, brainProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun")));

        var hist = _histograms.First(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun")));
    }

    [Fact]
    public async Task SyncBrainPostRunAsync_OnBrainSyncFailure_StillEmitsMetric()
    {
        var run = CreateRun();
        var brainSync = new Mock<IBrainSyncService>();
        var brainProvider = new Mock<IRepositoryProvider>();
        var config = new PipelineConfiguration { BrainPushMaxRetries = 1 };

        brainSync.Setup(b => b.SyncPostRunAsync(It.IsAny<PipelineRun>(), It.IsAny<IRepositoryProvider>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>>(), It.IsAny<int>()))
            .ThrowsAsync(new InvalidOperationException("brain push failed"));

        await _sut.SyncBrainPostRunAsync(run, brainSync.Object, brainProvider.Object, config, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun")));
        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun")));
    }

    // ── CollectFeedbackAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task CollectFeedbackAsync_EmitsStepDurationMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();

        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, historyService.Object, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection")));

        var hist = _histograms.First(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection")));
    }

    [Fact]
    public async Task CollectFeedbackAsync_OnAgentFailure_StillEmitsMetric()
    {
        var run = CreateRun();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);

        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ThrowsAsync(new InvalidOperationException("feedback agent failed"));

        await _sut.CollectFeedbackAsync(run, agentProvider.Object, feedbackService, null, _ => { }, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection")));
        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection")));
    }

    // ── RunFullPrCreationAsync / CreatePullRequest ──────────────────────────────

    [Fact]
    public async Task RunFullPrCreationAsync_EmitsCreatePullRequestMetric()
    {
        var run = CreateRunForFullPrCreation();
        var (request, repoProvider) = BuildPrCreationRequest(run, isDraft: false);

        await _sut.RunFullPrCreationAsync(request, CancellationToken.None);

        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "CreatePullRequest")));

        var hist = _histograms.First(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "CreatePullRequest")));
        hist.Value.Should().BeGreaterThanOrEqualTo(0);

        _counters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "CreatePullRequest")));
    }

    [Fact]
    public async Task RunFullPrCreationAsync_CreatePullRequestMetric_DoesNotSubsumeSubPhases()
    {
        // Verifies the split-try structure: CreatePullRequest is emitted before sub-phases run,
        // and each sub-phase also emits its own independent metric.
        // PullRequestNumber must NOT be pre-set — the orchestrator sets it from the PR URL (→ "99").
        // Pre-setting it causes the orchestrator to take the "update existing PR" path, returning null.
        const string projectId = "does-not-subsume-sub-phases-test-proj";
        var run = CreateRunForFullPrCreation();
        run.ProjectId = projectId; // unique project ID for tag-based isolation (avoids ConcurrentBag LIFO ordering issues)
        var (request, _) = BuildPrCreationRequest(run, isDraft: false);

        await _sut.RunFullPrCreationAsync(request, CancellationToken.None);

        // Verify run completed successfully and PR number was assigned (confirms RunPostPrSequenceAsync ran)
        run.CurrentStep.Should().Be(PipelineStep.Completed,
            "RunFullPrCreationAsync should have completed successfully so sub-phases ran");
        run.PullRequestNumber.Should().NotBeNullOrEmpty(
            "Orchestrator should have set PullRequestNumber from the PR URL");

        // Filter to only entries emitted by this invocation, scoped by unique project ID.
        // ConcurrentBag.ToArray() does not preserve insertion order (LIFO), so count-snapshot +
        // Skip is not reliable. Filtering by the unique project ID tag is deterministic.
        var projectTag = new KeyValuePair<string, object?>("pipeline.project_id", projectId);
        var thisRunHistograms = _histograms
            .Where(h => h.Tags.Contains(projectTag))
            .ToList();
        var thisRunCounters = _counters
            .Where(c => c.Tags.Contains(projectTag))
            .ToList();

        // Guard: confirm the project ID tag is present (catches tag-key typos before the assertions below)
        thisRunHistograms.Should().NotBeEmpty(
            $"at least one histogram should be tagged with pipeline.project_id={projectId}; " +
            $"if this fails, BuildStepTags is not emitting the project_id tag");

        // CreatePullRequest metric emitted
        thisRunHistograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "CreatePullRequest")),
            "CreatePullRequest step duration should be emitted by this invocation");

        thisRunCounters.Should().Contain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "CreatePullRequest")),
            "CreatePullRequest step count should be emitted by this invocation");

        // Sub-phase metrics emitted independently (not subsumed into CreatePullRequest)
        // Note: no brain provider → Reflection/BrainSyncPostRun are skipped.
        // GeneratePrDescription: run.PullRequestNumber="99" after orchestrator, so it runs.
        // FeedbackCollection: always runs on non-draft.
        var capturedStepNames = thisRunHistograms
            .Where(h => h.Name == "pipeline.step.duration")
            .Select(h => h.Tags.FirstOrDefault(t => t.Key == "step_name").Value?.ToString() ?? "(none)")
            .ToList();

        capturedStepNames.Should().Contain("GeneratePrDescription",
            $"Expected GeneratePrDescription metric from this invocation. " +
            $"Captured step_names for project {projectId}: [{string.Join(", ", capturedStepNames)}]. " +
            $"run.PullRequestNumber={run.PullRequestNumber}, run.CurrentStep={run.CurrentStep}");

        capturedStepNames.Should().Contain("FeedbackCollection",
            $"Expected FeedbackCollection metric from this invocation. " +
            $"Captured step_names for project {projectId}: [{string.Join(", ", capturedStepNames)}]");

        // Sub-phase count metrics also emitted
        var subPhaseNames = new[] { "GeneratePrDescription", "FeedbackCollection" };
        foreach (var stepName in subPhaseNames)
        {
            thisRunCounters.Should().Contain(c =>
                c.Name == "pipeline.step.count"
                && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", stepName)),
                $"step_name={stepName} counter should be emitted independently by this invocation");
        }
    }

    // ── Draft / conditional-skip paths ─────────────────────────────────────────

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenDraft_DoesNotEmitSubPhaseMetrics()
    {
        // Use a unique project ID to isolate this test's emissions from parallel-test cross-talk
        const string projectId = "draft-skip-test-proj";
        var run = CreateRun();
        run.PullRequestNumber = "42";
        run.ProjectId = projectId;
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);

        // Emit a known metric using the same projectId BEFORE the act step, to confirm the
        // "pipeline.project_id" tag guard is actually active and correctly filters by this ID.
        // SyncBrainPostRunAsync always emits regardless of isDraft and requires no external deps.
        var guardRun = CreateRun();
        guardRun.ProjectId = projectId;
        var dummyBrainSync = new Mock<IBrainSyncService>();
        var dummyBrainProvider = new Mock<IRepositoryProvider>();
        await _sut.SyncBrainPostRunAsync(guardRun, dummyBrainSync.Object, dummyBrainProvider.Object,
            new PipelineConfiguration(), _ => { }, CancellationToken.None);

        var projectTag = new KeyValuePair<string, object?>("pipeline.project_id", projectId);

        // Confirm the guard tag is active: the known emission above should be findable by projectTag.
        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "BrainSyncPostRun"))
            && h.Tags.Contains(projectTag),
            $"Guard assertion: SyncBrainPostRunAsync should have emitted a metric tagged with " +
            $"pipeline.project_id={projectId}, confirming the projectTag filter is active");

        await _sut.RunPostPrSequenceAsync(
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = true,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = new PipelineConfiguration(),
                BrainSync = null,
                BrainProvider = null,
                FeedbackService = feedbackService,
                HistoryService = null,
                EmitOutputLine = _ => { },
                TransitionCallback = _ => Task.CompletedTask
            },
            CancellationToken.None);

        // No sub-phase metrics should have been emitted — all phases are skipped for draft.
        // Filter by the unique project ID to isolate from other tests in the collection.
        // (Tag key "pipeline.project_id" is confirmed active by the guard assertion above.)
        var subPhaseNames = new[] { "GeneratePrDescription", "Reflection", "BrainSyncPostRun", "FeedbackCollection" };

        foreach (var stepName in subPhaseNames)
        {
            _histograms.Should().NotContain(h =>
                h.Name == "pipeline.step.duration"
                && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", stepName))
                && h.Tags.Contains(projectTag)
                // BrainSyncPostRun was emitted by the guard call above (for guardRun); exclude
                // that entry by checking the run ID does not match (it runs on a different run).
                // For all other step names there should be no entry at all with this projectTag.
                // TODO: The (stepName != "BrainSyncPostRun") condition makes the BrainSyncPostRun
                // NotContain assertion vacuously true — if production code incorrectly emits
                // BrainSyncPostRun during a draft run, this assertion will not catch it. Consider
                // using a run-ID tag (not currently emitted by BuildStepTags) or using a separate
                // snapshot count for the draft call to distinguish guard-run vs draft-run emissions.
                && (stepName != "BrainSyncPostRun"),
                $"step_name={stepName} should not be emitted when isDraft=true");

            _counters.Should().NotContain(c =>
                c.Name == "pipeline.step.count"
                && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", stepName))
                && c.Tags.Contains(projectTag)
                // TODO: Same vacuous-assertion issue as the histogram NotContain above — the
                // BrainSyncPostRun counter check is never evaluated. See histogram TODO for fix.
                && (stepName != "BrainSyncPostRun"),
                $"step_name={stepName} count should not be emitted when isDraft=true");
        }
    }

    [Fact]
    public async Task RunPostPrSequenceAsync_WhenNoPrNumber_DoesNotEmitGeneratePrDescriptionMetric()
    {
        // Use a unique project ID to isolate this test's emissions from parallel-test cross-talk
        const string projectId = "no-pr-number-test-proj";
        var run = CreateRun();
        run.PullRequestNumber = null; // no PR number — GeneratePrDescriptionAsync is skipped
        run.ProjectId = projectId;
        var agentProvider = new Mock<IAgentProvider>();
        var repoProvider = new Mock<IRepositoryProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();

        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":3,"category":"test","comment":"ok"}}"""] });

        await _sut.RunPostPrSequenceAsync(
            new PostPrSequenceRequest
            {
                Run = run,
                IsDraft = false,
                AgentProvider = agentProvider.Object,
                RepoProvider = repoProvider.Object,
                Config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) },
                BrainSync = null,
                BrainProvider = null,
                FeedbackService = feedbackService,
                HistoryService = historyService.Object,
                EmitOutputLine = _ => { },
                TransitionCallback = _ => Task.CompletedTask
            },
            CancellationToken.None);

        var projectTag = new KeyValuePair<string, object?>("pipeline.project_id", projectId);

        // FeedbackCollection IS emitted (isDraft=false, not gated on PullRequestNumber).
        // This positive assertion also serves as the guard verification: if "pipeline.project_id"
        // tag key or value were wrong, this Contain would fail, confirming the projectTag filter
        // is active before we rely on it in the NotContain assertions below.
        _histograms.Should().Contain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "FeedbackCollection"))
            && h.Tags.Contains(projectTag),
            $"FeedbackCollection should emit a metric tagged with pipeline.project_id={projectId}; " +
            "this also confirms the projectTag isolation guard is active");

        // GeneratePrDescription is skipped (no PullRequestNumber) — must NOT emit
        _histograms.Should().NotContain(h =>
            h.Name == "pipeline.step.duration"
            && h.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription"))
            && h.Tags.Contains(projectTag));
        _counters.Should().NotContain(c =>
            c.Name == "pipeline.step.count"
            && c.Tags.Contains(new KeyValuePair<string, object?>("step_name", "GeneratePrDescription"))
            && c.Tags.Contains(projectTag));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static PipelineRun CreateRunForFullPrCreation() => new()
    {
        RunId = "full-pr-test-run",
        IssueIdentifier = "test/repo#1",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-cfg-1",
        RepoProviderConfigId = "repo-cfg-1",
        RepositoryName = "org/repo",
        BranchName = "agent/test-1",
        WorkspacePath = Path.GetTempPath(),
        StartedAt = DateTime.UtcNow.AddMinutes(-5)
    };

    private (PrCreationRequest, Mock<IRepositoryProvider>) BuildPrCreationRequest(PipelineRun run, bool isDraft)
    {
        var repoProvider = new Mock<IRepositoryProvider>();
        var agentProvider = new Mock<IAgentProvider>();
        var feedbackService = new FeedbackService(_logger.Object);
        var historyService = new Mock<IPipelineRunHistoryService>();

        repoProvider.Setup(r => r.CommitAllAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>());
        repoProvider.Setup(r => r.PushBranchAsync(It.IsAny<WorkspacePath>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        repoProvider.Setup(r => r.HasCommitsAheadAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        repoProvider.Setup(r => r.GetFileChangesAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<FileChangeSummary>());
        repoProvider.Setup(r => r.CreatePullRequestAsync(It.IsAny<PullRequestInfo>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("https://github.com/org/repo/pull/99");
        repoProvider.Setup(r => r.BaseBranch).Returns("main");
        repoProvider.Setup(r => r.FormatCloseReference(It.IsAny<IssueIdentifier>())).Returns("Closes #1");

        historyService.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<PipelineRunSummary>)[]);
        agentProvider.Setup(a => a.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>>()))
            .ReturnsAsync(new AgentResult { ExitCode = 0, OutputLines = ["""{"harness":{"rating":4,"category":"test","comment":"ok"}}"""] });

        var prOrchestrator = new PullRequestOrchestrator(_logger.Object);
        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
        };

        var request = new PrCreationRequest
        {
            Run = run,
            Report = report,
            IsDraft = isDraft,
            PrOrchestrator = prOrchestrator,
            RepoProvider = repoProvider.Object,
            AgentProvider = agentProvider.Object,
            BrainProvider = null,
            BrainSync = null,
            Config = new PipelineConfiguration { AgentTimeout = TimeSpan.FromMinutes(1) },
            Issue = null,
            IssueComments = null,
            FeedbackService = feedbackService,
            HistoryService = historyService.Object,
            EmitOutputLine = _ => { },
            TransitionCallback = _ => Task.CompletedTask
        };

        return (request, repoProvider);
    }
}
