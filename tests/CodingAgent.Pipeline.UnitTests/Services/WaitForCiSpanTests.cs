using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests that <c>WaitForCi</c> spans are emitted for both the pre-PR CI path
/// (<see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/>) and the post-PR CI path
/// (<see cref="CiPollingCoordinator.WaitForPostPrCiAsync"/>).
/// </summary>
public class WaitForCiSpanTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly ConcurrentBag<Activity> _activities = [];

    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IPipelineProvider> _pipelineProvider = new();
    private readonly Mock<Serilog.ILogger> _logger = new();
    private readonly QualityGateExecutor _executor;
    private readonly CiPollingCoordinator _coordinator;

    private static readonly QualityGateReport PassingReport = new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "OK" },
        Tests = new GateResult { GateName = "Tests", Passed = true, Details = "OK" }
    };

    public WaitForCiSpanTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _executor = new QualityGateExecutor(
            new Mock<IQualityGateValidator>().Object,
            new PullRequestOrchestrator(_logger.Object),
            new CiLogWriter(_logger.Object),
            new FeedbackService(_logger.Object),
            _logger.Object);

        _coordinator = new CiPollingCoordinator(
            _logger.Object,
            new CiLogWriter(_logger.Object));

        SetupDefaultMocks();
    }

    public void Dispose() => _listener.Dispose();

    // ── Pre-PR CI path (AppendExternalCiIfNeededAsync → RunExternalCiPollAsync) ─

    [Fact]
    public async Task PrePrCiPath_EmitsWaitForCiSpan()
    {
        var (run, context) = BuildPrePrContext();
        SetupCiPass();

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        _activities.Should().Contain(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId),
            "AppendExternalCiIfNeededAsync must emit a WaitForCi span for the pre-PR CI path");
    }

    [Fact]
    public async Task PrePrCiPath_WaitForCiSpan_HasRunIdAndRunTypeTags()
    {
        var (run, context) = BuildPrePrContext();
        SetupCiPass();

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        span.GetTagItem("pipeline.run_id").Should().Be(run.RunId);
        span.GetTagItem("pipeline.run_type").Should().Be(run.RunType.ToString());
        span.GetTagItem("pipeline.ci_path").Should().Be("pre_pr");
    }

    [Fact]
    public async Task PrePrCiPath_CiPasses_WaitForCiSpan_HasPassedStatus()
    {
        var (run, context) = BuildPrePrContext();
        SetupCiPass();

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        span.GetTagItem("pipeline.ci_status").Should().Be("passed");
    }

    [Fact]
    public async Task PrePrCiPath_CiFails_WaitForCiSpan_HasNonPassedStatus()
    {
        var (run, context) = BuildPrePrContext();
        SetupCiFail();

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        var ciStatus = span.GetTagItem("pipeline.ci_status") as string;
        // TODO: [WARNING] This assertion only checks that pipeline.ci_status is not "passed" and not
        // null/empty. It does not verify the specific expected value (e.g., "failed"). If the production
        // code silently produces an empty string or an unexpected value due to a future state renaming,
        // this test would still pass. Consider asserting the exact expected value, e.g.:
        //   ciStatus.Should().Be("failed");
        ciStatus.Should().NotBeNullOrEmpty();
        ciStatus.Should().NotBe("passed");
    }

    [Fact]
    public async Task PrePrCiPath_OnException_WaitForCiSpan_HasErrorStatus()
    {
        var (run, context) = BuildPrePrContext();
        SetupCiThrows(new Exception("CI poll exploded"));

        // AppendExternalCiIfNeededAsync catches non-OCE exceptions and converts them to a gate result —
        // it does not rethrow. The catch block in RunExternalCiPollAsync still fires and sets span tags
        // before the outer handler absorbs the exception.
        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        span.GetTagItem("pipeline.ci_status").Should().Be("error",
            "the pre-PR WaitForCi catch block must set pipeline.ci_status = \"error\" when PollAndHandleInfraRetryAsync throws");
        // TODO: [WARNING] The zero-retry (default) path does not assert that pipeline.ci_infra_retries == 0.
        // A bug that always emits a constant non-zero value would go undetected here. Add:
        //   span.GetTagItem("pipeline.ci_infra_retries").Should().Be(0);
    }

    [Fact]
    public async Task PrePrCiPath_OnException_WaitForCiSpan_HasInfraRetryCount()
    {
        var (run, context) = BuildPrePrContext();

        // Simulate 2 infra retries having occurred before the exception by setting the count on the run
        // object via a mock callback. The catch block reads run.InfrastructureRetryCount at throw time.
        const int expectedRetries = 2;
        SetupCiThrows(new Exception("CI poll exploded"), infraRetriesBeforeThrow: expectedRetries, run);

        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        span.GetTagItem("pipeline.ci_infra_retries").Should().Be(expectedRetries,
            "the pre-PR WaitForCi catch block must set pipeline.ci_infra_retries from run.InfrastructureRetryCount");
    }

    // ── Post-PR CI path (CiPollingCoordinator.WaitForPostPrCiAsync) ───────────

    [Fact]
    public async Task PostPrCiPath_EmitsWaitForCiSpan()
    {
        var context = BuildPostPrContext();
        SetupCiPass();

        await _coordinator.WaitForPostPrCiAsync(context, PassingReport, notBefore: null, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        _activities.Should().Contain(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), context.Run.RunId),
            "WaitForPostPrCiAsync must emit a WaitForCi span for the post-PR CI path");
    }

    [Fact]
    public async Task PostPrCiPath_WaitForCiSpan_HasRunIdAndRunTypeTags()
    {
        var context = BuildPostPrContext();
        SetupCiPass();

        await _coordinator.WaitForPostPrCiAsync(context, PassingReport, notBefore: null, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), context.Run.RunId));
        span.GetTagItem("pipeline.run_id").Should().Be(context.Run.RunId);
        span.GetTagItem("pipeline.run_type").Should().Be(context.Run.RunType.ToString());
        span.GetTagItem("pipeline.ci_path").Should().Be("post_pr");
    }

    [Fact]
    public async Task PostPrCiPath_CiPasses_WaitForCiSpan_HasPassedStatus()
    {
        var context = BuildPostPrContext();
        SetupCiPass();

        await _coordinator.WaitForPostPrCiAsync(context, PassingReport, notBefore: null, CancellationToken.None);

        // Filter by run_id to avoid picking up WaitForCi spans emitted by other tests running in parallel.
        var span = _activities.First(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), context.Run.RunId));
        span.GetTagItem("pipeline.ci_status").Should().Be("passed");
    }

    // TODO: [WARNING] No test covers the post-PR CI failure path (CiPollingCoordinator.WaitForPostPrCiAsync
    // with a failing CI result). If pipeline.ci_status is silently wrong on a post-PR CI failure, no test
    // would catch it. Add: PostPrCiPath_CiFails_WaitForCiSpan_HasNonPassedStatus using SetupCiFail().

    // ── Helpers ───────────────────────────────────────────────────────────────

    private (PipelineRun run, QualityGateContext context) BuildPrePrContext()
    {
        var run = new PipelineRun
        {
            RunId = "wait-ci-pre-pr-test",
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Implementation,
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"wait-ci-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-wait-ci-test",
            ProjectId = null,
            ProjectName = null
        };

        var context = new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                MaxRetries = 0,
                MaxInfrastructureRetries = 0,
                ExternalCiTimeout = TimeSpan.FromSeconds(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                CiNotStartedMaxRetries = 0,
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50)
            },
            AgentProvider = Mock.Of<IAgentProvider>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            Callbacks = _callbacks.Object,
            RepoProvider = _repoProvider.Object,
            PipelineProvider = _pipelineProvider.Object,
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };

        return (run, context);
    }

    private QualityGateContext BuildPostPrContext()
    {
        var run = new PipelineRun
        {
            RunId = "wait-ci-post-pr-test",
            IssueIdentifier = "2",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            RunType = PipelineRunType.Implementation,
            WorkspacePath = Path.Combine(Path.GetTempPath(), $"wait-ci-postpr-{Guid.NewGuid():N}"),
            BranchName = "feature/auto-wait-ci-postpr-test",
            ProjectId = null,
            ProjectName = null
        };

        return new QualityGateContext
        {
            Run = run,
            Config = new PipelineConfiguration
            {
                MaxRetries = 0,
                MaxInfrastructureRetries = 0,
                ExternalCiTimeout = TimeSpan.FromSeconds(5),
                CiNotStartedTimeout = TimeSpan.FromMilliseconds(50),
                CiNotStartedMaxRetries = 0,
                ExternalCiPollInterval = TimeSpan.FromMilliseconds(50)
            },
            AgentProvider = Mock.Of<IAgentProvider>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            Callbacks = _callbacks.Object,
            RepoProvider = _repoProvider.Object,
            PipelineProvider = _pipelineProvider.Object,
            QualityGateConfigs = new List<QualityGateConfiguration>()
        };
    }

    private void SetupDefaultMocks()
    {
        _repoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _repoProvider.Setup(r => r.CommitAllAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<string>?>(),
                true, It.IsAny<CancellationToken>(), It.IsAny<IReadOnlyList<string>?>()))
            .ReturnsAsync(Array.Empty<string>() as IReadOnlyList<string>);
        _repoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.PushBranchAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<BranchName>(), true, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repoProvider.Setup(r => r.GetHeadCommitShaAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("sha-head-test");
        _callbacks.Setup(c => c.CreateDraftPrIfNotExists(
                It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
    }

    private void SetupCiPass()
    {
        _pipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        _pipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Passed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Passed }]
            });
    }

    private void SetupCiFail()
    {
        _pipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        _pipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Failed,
                Jobs = [new() { Name = "build", State = PipelineRunState.Failed }]
            });
    }

    /// <summary>
    /// Configures WaitForCompletionAsync to throw <paramref name="exception"/>.
    /// When <paramref name="infraRetriesBeforeThrow"/> is greater than zero, the mock uses a
    /// callback to set <see cref="PipelineRun.InfrastructureRetryCount"/> on <paramref name="run"/>
    /// before throwing, simulating infrastructure retries that incremented the counter before the
    /// final exception.
    /// </summary>
    private void SetupCiThrows(Exception exception, int infraRetriesBeforeThrow = 0, PipelineRun? run = null)
    {
        _pipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        if (infraRetriesBeforeThrow > 0 && run != null)
        {
            // TODO: [WARNING] The lambda inside Callback() implicitly assumes run is non-null, but the
            // parameter is typed PipelineRun?. The outer guard prevents the null path today, but a future
            // caller that passes infraRetriesBeforeThrow > 0 with run == null would get a NullReferenceException
            // at test execution time rather than compile time. Consider adding a null-guard inside the lambda or
            // making run non-nullable when infraRetriesBeforeThrow > 0 is required.
            _pipelineProvider.Setup(p => p.WaitForCompletionAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .Callback(() => run.InfrastructureRetryCount = infraRetriesBeforeThrow)
                .ThrowsAsync(exception);
        }
        else
        {
            _pipelineProvider.Setup(p => p.WaitForCompletionAsync(
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(exception);
        }
    }
}
