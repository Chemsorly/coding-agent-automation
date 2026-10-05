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

    // TODO: [WARNING] Neither the pre-PR nor the post-PR WaitForCi tests cover the exception/throw path.
    // The catch blocks in QualityGateExecutor.ExternalCi.cs and CiPollingCoordinator set
    // pipeline.ci_status = "error" and rethrow. No test verifies this, so accidentally removing the catch
    // block or the tag assignment would go undetected. Add tests that configure the pipeline provider to
    // throw and assert pipeline.ci_status == "error" on the resulting span.

    // ── pre-PR CI error path: pipeline.ci_infra_retries tag parity ───────────

    // TODO: [WARNING] The catch block in RunExternalCiPollAsync is also reached when GetRunStatusAsync
    // throws during the appear-wait loop (not only when WaitForCompletionAsync throws). There is no test
    // covering that path, so a regression where SetTag("pipeline.ci_infra_retries", ...) is accidentally
    // placed only inside the WaitForCompletionAsync-specific branch would go undetected. Consider adding
    // a test that configures GetRunStatusAsync to throw and asserts pipeline.ci_infra_retries is still
    // set on the resulting WaitForCi span.

    [Fact]
    public async Task PrePrCiPath_WhenPollThrows_WaitForCiSpan_HasCiInfraRetriesTag()
    {
        // Arrange: CI runs appear but WaitForCompletion throws, which propagates through
        // PollAndHandleInfraRetryAsync and is caught by RunExternalCiPollAsync's catch block.
        var (run, context) = BuildPrePrContext();
        // TODO: [WARNING] This test pre-seeds run.InfrastructureRetryCount = 2 before the call, which
        // means it validates that the catch block reads from run.InfrastructureRetryCount, but does not
        // validate that PollAndHandleInfraRetryAsync correctly mutates the count before the throw. A
        // regression in the increment logic inside PollAndHandleInfraRetryAsync could go undetected
        // because the count is already set here. Consider an additional test that leaves
        // InfrastructureRetryCount at 0 and verifies it reflects actual retry mutations.
        run.InfrastructureRetryCount = 2;

        // GetRunStatusAsync returns Running so runs "appear" and WaitForCiRunsToAppearAsync succeeds.
        _pipelineProvider.Setup(p => p.GetRunStatusAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineRunStatus
            {
                State = PipelineRunState.Running,
                Jobs = [new() { Name = "build", State = PipelineRunState.Running }]
            });

        // WaitForCompletionAsync throws — this propagates to RunExternalCiPollAsync's catch block.
        _pipelineProvider.Setup(p => p.WaitForCompletionAsync(
                It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<TimeSpan>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("transient CI infrastructure failure"));

        // Act: AppendExternalCiIfNeededAsync catches the rethrow and converts it to a gate result.
        await _executor.AppendExternalCiIfNeededAsync(
            context, PassingReport, allowEmptyCommit: false, CancellationToken.None);

        // Assert: the WaitForCi span must carry pipeline.ci_infra_retries, matching the post-PR path.
        var span = _activities.FirstOrDefault(a => a.DisplayName == "WaitForCi"
            && Equals(a.GetTagItem("pipeline.run_id"), run.RunId));
        span.Should().NotBeNull("AppendExternalCiIfNeededAsync must emit a WaitForCi span even when polling throws");
        // TODO: [WARNING] Activity.GetTagItem returns object, and the stored type depends on how SetTag
        // serialises the value (boxed int vs string). If the production code stores the tag as a string
        // "2" rather than an int 2, the Be(2) assertion below would fail even though the tag is correct.
        // Verify or document the expected stored type and update the assertion (e.g. Be("2") or cast) to
        // make the comparison unambiguous and resilient to storage-type changes.
        span!.GetTagItem("pipeline.ci_infra_retries").Should().Be(2,
            "pipeline.ci_infra_retries must be set on the pre-PR error span to match the post-PR path (issue #3346)");
        span.GetTagItem("pipeline.ci_status").Should().Be("error");
    }

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
}
