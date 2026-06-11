using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for PipelineRunLifecycleService.
/// Feature: 017-pipeline-run-lifecycle-service
/// </summary>
public class PipelineRunLifecycleServiceTests
{
    private readonly Mock<IPipelineRunHistoryService> _mockHistory;
    private readonly Mock<IOrchestratorRunService> _mockRunService;
    private readonly Mock<Serilog.ILogger> _mockLogger;

    public PipelineRunLifecycleServiceTests()
    {
        _mockHistory = new Mock<IPipelineRunHistoryService>();
        _mockRunService = new Mock<IOrchestratorRunService>();
        _mockLogger = new Mock<Serilog.ILogger>();
    }

    private PipelineRunLifecycleService CreateService(IOrchestratorRunService? runService = null)
    {
        return new PipelineRunLifecycleService(
            _mockHistory.Object,
            runService ?? _mockRunService.Object,
            _mockLogger.Object);
    }

    private static PipelineRun CreateRun(string runId = "run-1", string issueId = "issue-1", PipelineStep step = PipelineStep.Created)
    {
        return new PipelineRun
        {
            RunId = runId,
            IssueIdentifier = issueId,
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            CurrentStep = step,
            HighWaterMark = step,
            StartedAt = DateTime.UtcNow
        };
    }

    // ── Constructor Validation ──────────────────────────────────────────

    [Fact]
    public void Constructor_NullLogger_ThrowsArgumentNullException()
    {
        var act = () => new PipelineRunLifecycleService(
            _mockHistory.Object,
            _mockRunService.Object,
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_NullHistoryService_ThrowsArgumentNullException()
    {
        var act = () => new PipelineRunLifecycleService(
            null!,
            _mockRunService.Object,
            _mockLogger.Object);

        act.Should().Throw<ArgumentNullException>().WithParameterName("historyService");
    }

    // ── AddRunToHistory ─────────────────────────────────────────────────

    [Fact]
    public void AddRunToHistory_DelegatesToHistoryService()
    {
        var service = CreateService();
        var run = CreateRun();

        service.AddRunToHistory(run);

        _mockHistory.Verify(h => h.AddRunToHistory(run), Times.Once);
    }

    // ── RegisterDispatchedRun ────────────────────────────────────────────

    [Fact]
    public void RegisterDispatchedRun_WhenNoRunService_ThrowsInvalidOperationException()
    {
        var service = new PipelineRunLifecycleService(
            _mockHistory.Object,
            runService: null,
            _mockLogger.Object);

        var run = CreateRun();

        var act = () => service.RegisterDispatchedRun(run);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*OrchestratorRunService*not configured*");
    }

    // ── Dispose / DisposeAsync ──────────────────────────────────────────

    [Fact]
    public void Dispose_DisposesTokenSource()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(cts.Token);

        service.Dispose();

        // After dispose, accessing the CTS should indicate it's disposed
        // The service's CTS property should still be accessible but the underlying CTS is disposed
        var act = () => service.CancellationTokenSource!.Token;
        act.Should().Throw<ObjectDisposedException>();

        cts.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DisposesTokenSource()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(cts.Token);

        await service.DisposeAsync();

        var act = () => service.CancellationTokenSource!.Token;
        act.Should().Throw<ObjectDisposedException>();

        cts.Dispose();
    }

    // ── ClearEventSubscribers ───────────────────────────────────────────

    [Fact]
    public void ClearEventSubscribers_RemovesAllHandlers()
    {
        var service = new TestablePipelineRunLifecycleService(
            _mockHistory.Object,
            _mockRunService.Object,
            _mockLogger.Object);

        var changeFired = false;
        var outputFired = false;
        var chatResponseFired = false;
        var chatCompletedFired = false;

        service.OnChange += () => changeFired = true;
        service.OnOutputLine += _ => outputFired = true;
        service.OnChatResponse += (_, _) => chatResponseFired = true;
        service.OnChatCompleted += (_, _, _) => chatCompletedFired = true;

        service.InvokeClearEventSubscribers();

        // After clearing, events should not fire
        service.NotifyChange();
        service.EmitOutputLine("test");
        service.NotifyChatResponse("s1", new List<string>().AsReadOnly());
        service.NotifyChatCompleted("s1", 0, null);

        changeFired.Should().BeFalse();
        outputFired.Should().BeFalse();
        chatResponseFired.Should().BeFalse();
        chatCompletedFired.Should().BeFalse();
    }

    // ── CancelPipelineAsync No-Op ───────────────────────────────────────

    [Fact]
    public async Task CancelPipelineAsync_WhenNoActiveRun_IsNoOp()
    {
        var service = CreateService();
        service.ActiveRun = null;

        await service.CancelPipelineAsync();

        _mockHistory.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);
    }

    [Fact]
    public async Task CancelPipelineAsync_WhenRunInTerminalState_IsNoOp()
    {
        var service = CreateService();
        service.ActiveRun = CreateRun(step: PipelineStep.Completed);

        await service.CancelPipelineAsync();

        _mockHistory.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);
    }

    // ── MarkAgentRunsCancelled No-Op ────────────────────────────────────

    [Fact]
    public async Task MarkAgentRunsCancelled_WhenNoRunService_IsNoOp()
    {
        var service = new PipelineRunLifecycleService(
            _mockHistory.Object,
            runService: null,
            _mockLogger.Object);

        var result = await service.MarkAgentRunsCancelled();

        result.Should().BeEmpty();
        _mockHistory.Verify(h => h.AddRunToHistory(It.IsAny<PipelineRun>()), Times.Never);
    }

    // ── MarkAgentRunsCancelled Run Removal ──────────────────────────────

    [Fact]
    public async Task MarkAgentRunsCancelled_RemovesRunsFromActiveTracking()
    {
        var runService = new Orchestration.OrchestratorRunService(_mockLogger.Object);
        var run1 = CreateRun("run-1", "issue-1", PipelineStep.GeneratingCode);
        var run2 = CreateRun("run-2", "issue-2", PipelineStep.CloningRepository);
        runService.AddRun(run1);
        runService.AddRun(run2);

        var service = CreateService(runService);

        await service.MarkAgentRunsCancelled();

        runService.GetActiveRuns().Should().BeEmpty();
        runService.ActiveRunCount.Should().Be(0);
    }

    [Fact]
    public async Task MarkAgentRunsCancelled_IsIssueBeingProcessed_ReturnsFalseForCancelledIssues()
    {
        var runService = new Orchestration.OrchestratorRunService(_mockLogger.Object);
        var run1 = CreateRun("run-1", "issue-1", PipelineStep.GeneratingCode);
        var run2 = CreateRun("run-2", "issue-2", PipelineStep.AnalyzingCode);
        runService.AddRun(run1);
        runService.AddRun(run2);

        var service = CreateService(runService);

        var cancelledIssues = await service.MarkAgentRunsCancelled();

        cancelledIssues.Should().Contain(("issue-1", "ip-1"));
        cancelledIssues.Should().Contain(("issue-2", "ip-1"));
        runService.IsIssueBeingProcessed("issue-1", "ip-1").Should().BeFalse();
        runService.IsIssueBeingProcessed("issue-2", "ip-1").Should().BeFalse();
    }

    // ── TransitionTo HighWaterMark with StepOrder ─────────────────────────

    [Fact]
    public void TransitionTo_RunningEnvironmentSetup_AdvancesHighWaterMarkPastCloningRepository()
    {
        // RunningEnvironmentSetup has enum ordinal 28 but logical order 2 (after CloningRepository=1).
        // Before the StepOrder fix, ordinal-based comparison would have worked by accident here,
        // but this test proves the StepOrder-based logic correctly advances HWM.
        var service = CreateService();
        var run = CreateRun(step: PipelineStep.CloningRepository);

        service.TransitionTo(run, PipelineStep.RunningEnvironmentSetup);

        run.CurrentStep.Should().Be(PipelineStep.RunningEnvironmentSetup);
        run.HighWaterMark.Should().Be(PipelineStep.RunningEnvironmentSetup);
    }

    [Fact]
    public void TransitionTo_EarlierStep_DoesNotRegressHighWaterMark()
    {
        // If a run transitions backward (e.g. retry), HWM should not regress
        var service = CreateService();
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        run.HighWaterMark = PipelineStep.GeneratingCode;

        service.TransitionTo(run, PipelineStep.AnalyzingCode);

        run.CurrentStep.Should().Be(PipelineStep.AnalyzingCode);
        run.HighWaterMark.Should().Be(PipelineStep.GeneratingCode); // unchanged
    }

    /// <summary>
    /// Test subclass to access protected ClearEventSubscribers method.
    /// </summary>
    private sealed class TestablePipelineRunLifecycleService : PipelineRunLifecycleService
    {
        public TestablePipelineRunLifecycleService(
            IPipelineRunHistoryService historyService,
            IOrchestratorRunService? runService,
            Serilog.ILogger logger)
            : base(historyService, runService, logger) { }

        public void InvokeClearEventSubscribers() => ClearEventSubscribers();
    }
}
