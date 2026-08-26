using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.UnitTests.Pipeline;

/// <summary>
/// Behavioral unit tests for <see cref="PipelineOrchestrationService"/>.
/// Covers CancelPipelineAsync and ReleaseActiveAgentRunsAsync.
/// Risk score was 31,200 (104 churn × coupling² × 3 impact); both methods were line-rate=0.
/// </summary>
public class PipelineOrchestrationServiceBehaviorTests
{
    private static PipelineRun MakeActiveRun(PipelineRunType runType = PipelineRunType.Implementation) =>
        new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = DateTime.UtcNow,
            RunType = runType,
            CurrentStep = PipelineStep.GeneratingCode // non-terminal → IsRunning = true
        };

    private static PipelineRunLifecycleService MakeLifecycle(IOrchestratorRunService? runService = null) =>
        new(
            historyService: new TestOrchestrationFactory.NullHistoryService(),
            runService: runService,
            logger: Serilog.Log.Logger);

    // ── CancelPipelineAsync — with active run ────────────────────────────

    [Fact]
    public async Task CancelPipelineAsync_WithActiveImplementationRun_NullProvider_DelegatesToLifecycle()
    {
        // Arrange: active Implementation run, no active issue provider
        // Label swap skipped (no provider + not Review); lifecycle.CancelPipelineAsync still runs.
        var lifecycle = MakeLifecycle();
        var run = MakeActiveRun();
        lifecycle.ActiveRun = run;

        var mockLabelService = new Mock<ILabelService>();

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            labelService: mockLabelService.Object,
            lifecycle: lifecycle);

        // Act
        await svc.CancelPipelineAsync();

        // Assert: lifecycle applied the cancel (run moves to Cancelled)
        run.CurrentStep.Should().Be(PipelineStep.Cancelled,
            "lifecycle.CancelPipelineAsync must transition the run to Cancelled");

        // Label swap must NOT fire when ActiveIssueProvider is null and run is not Review
        mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "label swap must be skipped when no active issue provider and run is not a Review run");
    }

    [Fact]
    public async Task CancelPipelineAsync_ReviewRun_SwapsLabel()
    {
        // Arrange: Review run — label swap fires even without an active issue provider
        var lifecycle = MakeLifecycle();
        var run = MakeActiveRun(PipelineRunType.Review);
        lifecycle.ActiveRun = run;

        var mockLabelService = new Mock<ILabelService>();
        mockLabelService
            .Setup(l => l.SwapLabelAsync(
                It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
                It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            labelService: mockLabelService.Object,
            lifecycle: lifecycle);

        // Act
        await svc.CancelPipelineAsync();

        // Assert: label swap fires for Review runs (condition: run.RunType == Review)
        mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(),
            It.Is<IssueIdentifier>(id => id.Value == run.IssueIdentifier),
            AgentLabels.Cancelled,
            It.IsAny<LabelTargetKind>(),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "CancelPipelineAsync must swap label for Review runs regardless of issue provider state");
    }

    [Fact]
    public async Task CancelPipelineAsync_AlreadyAtTerminalStep_IsNoOp()
    {
        // Arrange: run already completed — IsRunning returns false
        var lifecycle = MakeLifecycle();
        var run = MakeActiveRun();
        run.CurrentStep = PipelineStep.Completed; // terminal → IsRunning = false
        lifecycle.ActiveRun = run;

        var mockLabelService = new Mock<ILabelService>();

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            labelService: mockLabelService.Object,
            lifecycle: lifecycle);

        // Act
        var ex = await Record.ExceptionAsync(() => svc.CancelPipelineAsync());

        ex.Should().BeNull();
        run.CurrentStep.Should().Be(PipelineStep.Completed, "terminal run must not be mutated");
        mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CancelPipelineAsync_NullActiveRun_IsNoOp()
    {
        var mockLabelService = new Mock<ILabelService>();

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            labelService: mockLabelService.Object);

        var ex = await Record.ExceptionAsync(() => svc.CancelPipelineAsync());

        ex.Should().BeNull("null ActiveRun must be a safe no-op");
        mockLabelService.Verify(l => l.SwapLabelAsync(
            It.IsAny<ProviderConfigId>(), It.IsAny<IssueIdentifier>(), It.IsAny<string>(),
            It.IsAny<LabelTargetKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── ReleaseActiveAgentRunsAsync ──────────────────────────────────────

    [Fact]
    public async Task ReleaseActiveAgentRunsAsync_WithActiveRuns_RemovesFromTracking()
    {
        var runService = new OrchestratorRunService(Mock.Of<Serilog.ILogger>());
        var run1 = new PipelineRun
        {
            RunId = "run-1", IssueIdentifier = "org/repo#1", IssueTitle = "T1",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation, StartedAt = DateTime.UtcNow
        };
        var run2 = new PipelineRun
        {
            RunId = "run-2", IssueIdentifier = "org/repo#2", IssueTitle = "T2",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation, StartedAt = DateTime.UtcNow
        };
        runService.AddRun(run1);
        runService.AddRun(run2);

        var lifecycle = MakeLifecycle(runService);

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            lifecycle: lifecycle);

        await svc.ReleaseActiveAgentRunsAsync();

        runService.GetActiveRuns().Should().BeEmpty(
            "ReleaseActiveAgentRunsAsync must remove all agent-dispatched runs for graceful handoff");
    }

    [Fact]
    public async Task ReleaseActiveAgentRunsAsync_WithNoRuns_CompletesWithoutError()
    {
        var runService = new OrchestratorRunService(Mock.Of<Serilog.ILogger>());
        var lifecycle = MakeLifecycle(runService);

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            lifecycle: lifecycle);

        var ex = await Record.ExceptionAsync(() => svc.ReleaseActiveAgentRunsAsync());

        ex.Should().BeNull("empty release must be a safe no-op");
    }

    [Fact]
    public async Task ReleaseActiveAgentRunsAsync_DoesNotWriteHistory()
    {
        // Strict mock — any history write call causes failure
        var runService = new OrchestratorRunService(Mock.Of<Serilog.ILogger>());
        runService.AddRun(new PipelineRun
        {
            RunId = "run-handoff", IssueIdentifier = "org/repo#1", IssueTitle = "T",
            IssueProviderConfigId = "ip-1", RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Implementation, StartedAt = DateTime.UtcNow
        });

        var mockHistory = new Mock<IPipelineRunHistoryService>(MockBehavior.Strict);
        var lifecycle = new PipelineRunLifecycleService(
            historyService: mockHistory.Object,
            runService: runService,
            logger: Serilog.Log.Logger);

        await using var svc = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            lifecycle: lifecycle);

        // Act + Assert: strict mock will throw if history is touched
        var ex = await Record.ExceptionAsync(() => svc.ReleaseActiveAgentRunsAsync());
        ex.Should().BeNull(
            "ReleaseActiveAgentRunsAsync must NOT write history — " +
            "agents will complete normally on the new pod after a rolling update");
    }
}
