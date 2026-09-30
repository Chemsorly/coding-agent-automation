using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests the telemetry spans emitted by <see cref="VerifyBaselineStep"/>:
/// - A VerifyBaseline span is always emitted when the step runs.
/// - Fatal health-check failure sets Error on the VerifyBaseline span.
/// - Non-critical workspace baseline failure adds exception event WITHOUT setting Error.
/// </summary>
public class VerifyBaselineStepSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];

    private readonly Mock<IQualityGateValidator> _validator = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    // Per-test unique run ID so parallel tests don't pick up each other's spans.
    private string _testRunId = string.Empty;

    public VerifyBaselineStepSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task ExecuteAsync_AlwaysEmitsVerifyBaselineSpan()
    {
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        _activities.Should().Contain(a => a.DisplayName == "VerifyBaseline",
            "VerifyBaselineStep must emit a VerifyBaseline span");
    }

    [Fact]
    public async Task ExecuteAsync_SpanHasPipelineRunIdTag()
    {
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        var span = GetOwnSpan("VerifyBaseline");
        span.GetTagItem("pipeline.run_id").Should().Be(_testRunId);
        span.GetTagItem("pipeline.issue").Should().NotBeNull();
    }

    [Fact]
    public async Task ExecuteAsync_HealthCheckFails_SetsErrorOnVerifyBaselineSpan()
    {
        // Fatal failure: agent health check throws → span must have Error status.
        var agentProviderMock = new Mock<IAgentProvider>();
        agentProviderMock.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("agent binary not found"));

        var context = BuildContext(agentProviderMock: agentProviderMock, preResolvedQgcs: [CreateQgc()]);

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        var span = GetOwnSpan("VerifyBaseline");
        span.Status.Should().Be(ActivityStatusCode.Error,
            "a fatal agent health-check failure must set Error on the VerifyBaseline span");
        span.StatusDescription.Should().Contain("agent binary not found");
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceBaselineThrows_NoErrorOnVerifyBaselineSpan()
    {
        // Non-fatal failure: workspace baseline check throws → span must NOT have Error status.
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("build tool not found"));

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        var span = GetOwnSpan("VerifyBaseline");
        span.Status.Should().NotBe(ActivityStatusCode.Error,
            "a non-critical workspace baseline failure must NOT set Error on the span");
    }

    [Fact]
    public async Task ExecuteAsync_WorkspaceBaselineThrows_AddsNonCriticalExceptionEvent()
    {
        // Non-fatal failure: workspace baseline check throws → exception event with pipeline.non_critical=true.
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("compilation failed"));

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        var span = GetOwnSpan("VerifyBaseline");
        var evt = span.Events.Should().ContainSingle(e => e.Name == "exception").Which;
        evt.Tags.Should().Contain(t => t.Key == "pipeline.non_critical" && true.Equals(t.Value));
    }

    [Fact]
    public async Task ExecuteAsync_HealthCheckSucceeds_SpanHasNoErrorStatus()
    {
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        await new VerifyBaselineStep().ExecuteAsync(context, CancellationToken.None);

        var span = GetOwnSpan("VerifyBaseline");
        span.Status.Should().NotBe(ActivityStatusCode.Error,
            "a healthy baseline must not set Error on the span");
        span.Events.Should().BeEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the first span with <paramref name="displayName"/> that was emitted by this
    /// test instance (matched by <see cref="_testRunId"/>). Filters out spans from other tests
    /// that run in parallel and share the same global ActivityListener.
    /// </summary>
    private Activity GetOwnSpan(string displayName) =>
        _activities.First(a => a.DisplayName == displayName
                                && _testRunId.Equals(a.GetTagItem("pipeline.run_id")));

    private PipelineStepContext BuildContext(
        Mock<IAgentProvider>? agentProviderMock = null,
        IReadOnlyList<QualityGateConfiguration>? preResolvedQgcs = null)
    {
        // Assign a unique RunId per test so that span-filter assertions are not confused by
        // spans emitted by other tests running concurrently (which use Guid-based RunIds).
        _testRunId = $"test-run-{Guid.NewGuid():N}";

        var run = new PipelineRun
        {
            RunId = _testRunId,
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.CreatingBranch,
            WorkspacePath = "/tmp/workspace",
            RepositoryName = "owner/repo"
        };

        var agentProvider = agentProviderMock?.Object ?? Mock.Of<IAgentProvider>();
        // By default, health check succeeds.
        if (agentProviderMock == null)
        {
            var defaultAgent = new Mock<IAgentProvider>();
            defaultAgent.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            agentProvider = defaultAgent.Object;
        }

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp",
                BaselineHealthCheckEnabled = true
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = agentProvider,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = _validator.Object,
            PreResolvedQualityGateConfigs = preResolvedQgcs
        };
    }

    private static QualityGateConfiguration CreateQgc() => new()
    {
        Id = "qgc-1",
        DisplayName = "TestQgc",
        CompilationCommand = "dotnet build",
        TestCommand = "dotnet test"
    };

    private static QualityGateReport CreatePassingReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "ok" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "ok" }
    };
}
