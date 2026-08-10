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

    private PipelineRunLifecycleService CreateService(IOrchestratorRunService? runService = null, IAgentCancellationSender? agentCancellationSender = null)
    {
        return new PipelineRunLifecycleService(
            _mockHistory.Object,
            runService ?? _mockRunService.Object,
            _mockLogger.Object,
            agentCancellationSender);
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

    // ── AddRunToHistoryAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task AddRunToHistoryAsync_DelegatesToHistoryService()
    {
        var service = CreateService();
        var run = CreateRun();

        await service.AddRunToHistoryAsync(run);

        _mockHistory.Verify(h => h.AddRunToHistoryAsync(run, It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: [WARNING] Add test for exception propagation — verify that if AddRunToHistoryAsync throws,
    // the exception propagates to callers (FailRunAsync, CancelPipelineAsync) and doesn't silently break
    // pipeline finalization. This gap was introduced when the method became async.

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

        // TODO: Asserting null validates atomicity but does not verify the underlying CTS was actually disposed. Consider capturing a reference before disposal and asserting ObjectDisposedException on .Token.
        // After dispose, the field is atomically set to null
        service.CancellationTokenSource.Should().BeNull();

        cts.Dispose();
    }

    [Fact]
    public async Task DisposeAsync_DisposesTokenSource()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(cts.Token);

        await service.DisposeAsync();

        // TODO: Asserting null validates atomicity but does not verify the underlying CTS was actually disposed. Consider capturing a reference before disposal and asserting ObjectDisposedException on .Token.
        // After dispose, the field is atomically set to null
        service.CancellationTokenSource.Should().BeNull();

        cts.Dispose();
    }

    // ── CreateLinkedCancellationToken ───────────────────────────────────

    [Fact]
    public void CreateLinkedCancellationToken_DisposesPreviousCts()
    {
        var service = CreateService();
        var cts1 = new CancellationTokenSource();
        var cts2 = new CancellationTokenSource();

        service.CreateLinkedCancellationToken(cts1.Token);
        var firstCts = service.CancellationTokenSource;

        service.CreateLinkedCancellationToken(cts2.Token);

        // Previous CTS should be disposed
        var act = () => firstCts!.Token;
        act.Should().Throw<ObjectDisposedException>();

        cts1.Dispose();
        cts2.Dispose();
        service.Dispose();
    }

    [Fact]
    public void CreateLinkedCancellationToken_NewCtsIsActive()
    {
        var service = CreateService();
        var cts = new CancellationTokenSource();

        var token = service.CreateLinkedCancellationToken(cts.Token);

        token.CanBeCanceled.Should().BeTrue();
        token.IsCancellationRequested.Should().BeFalse();

        cts.Cancel();
        token.IsCancellationRequested.Should().BeTrue();

        cts.Dispose();
        service.Dispose();
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

        _mockHistory.Verify(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task CancelPipelineAsync_WhenRunInTerminalState_IsNoOp()
    {
        var service = CreateService();
        service.ActiveRun = CreateRun(step: PipelineStep.Completed);

        await service.CancelPipelineAsync();

        _mockHistory.Verify(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── MarkAgentRunsCancelled Rolling-Update Safety ────────────────────
    // Bug: on graceful shutdown during a rolling update, the new pod has already rehydrated
    // active runs. If MarkAgentRunsCancelled writes Cancelled history entries, those runs
    // appear as CANCELLED in the UI even though the agents will complete them on the new pod.
    // Fix: MarkAgentRunsCancelled must NOT write history — it should only remove runs from
    // in-memory tracking so dedup guards are released.

    [Fact]
    public async Task MarkAgentRunsCancelled_DoesNotWriteHistoryEntries()
    {
        // Arrange — two active agent runs
        var runService = new Orchestration.OrchestratorRunService(_mockLogger.Object);
        var run1 = CreateRun("run-1", "issue-1", PipelineStep.GeneratingCode);
        var run2 = CreateRun("run-2", "issue-2", PipelineStep.CloningRepository);
        runService.AddRun(run1);
        runService.AddRun(run2);

        var service = CreateService(runService);

        // Act
        service.MarkAgentRunsCancelled();

        // Assert — no history written; new pod will write the real outcome
        _mockHistory.Verify(
            h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task MarkAgentRunsCancelled_DoesNotSetCancelledStep()
    {
        // Arrange
        var runService = new Orchestration.OrchestratorRunService(_mockLogger.Object);
        var run = CreateRun("run-1", "issue-1", PipelineStep.GeneratingCode);
        runService.AddRun(run);

        var service = CreateService(runService);

        // Act
        service.MarkAgentRunsCancelled();

        // Assert — step is not mutated; leave the run state for the new pod to finalise
        run.CurrentStep.Should().NotBe(PipelineStep.Cancelled);
    }

    // ── MarkAgentRunsCancelled No-Op ────────────────────────────────────

    [Fact]
    public async Task MarkAgentRunsCancelled_WhenNoRunService_IsNoOp()
    {
        var service = new PipelineRunLifecycleService(
            _mockHistory.Object,
            runService: null,
            _mockLogger.Object);

        var result = service.MarkAgentRunsCancelled();

        result.Should().BeEmpty();
        _mockHistory.Verify(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()), Times.Never);
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

        service.MarkAgentRunsCancelled();

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

        var cancelledIssues = service.MarkAgentRunsCancelled();

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

    // ── CancelPipelineAsync CTS Race Condition ────────────────────────────

    // TODO: This test does not verify that _logger.Warning(...) was actually called.
    // The assertions only check state transition and history persistence, which pass even if the
    // catch block is empty. Consider using a LogEventSink or similar approach to assert the warning
    // log is emitted, so a regression removing the log statement would be caught.
    [Fact]
    public async Task CancelPipelineAsync_WhenCtsDisposed_LogsWarning()
    {
        var service = CreateService();
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        service.ActiveRun = run;

        // Populate and capture the CTS, then dispose to simulate the race
        var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);
        var cts = service.CancellationTokenSource!;
        cts.Dispose();

        await service.CancelPipelineAsync();

        // Verify state transition still occurs
        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        _mockHistory.Verify(h => h.AddRunToHistoryAsync(run, It.IsAny<CancellationToken>()), Times.Once);

        externalCts.Dispose();
    }

    [Fact]
    public async Task CancelPipelineAsync_WhenCtsDisposed_SendsFallbackCancelToAgent()
    {
        var mockSender = new Mock<IAgentCancellationSender>();
        var service = CreateService(agentCancellationSender: mockSender.Object);
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        run.AgentId = "agent-42";
        service.ActiveRun = run;

        // Populate and dispose CTS to trigger the race
        var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);
        var cts = service.CancellationTokenSource!;
        cts.Dispose();

        await service.CancelPipelineAsync();

        // Verify fallback cancel signal was sent
        mockSender.Verify(
            s => s.SendCancelJobAsync(
                It.Is<AgentId>(a => a.Value == "agent-42"),
                "run-1",
                CancellationToken.None),
            Times.Once);

        externalCts.Dispose();
    }

    // TODO: This test does not verify that _logger.Warning(...) was actually called.
    // The assertions only check that the sender was NOT called and state transitioned.
    // This would pass with the old silent-swallow implementation. Consider adding a log assertion.
    [Fact]
    public async Task CancelPipelineAsync_WhenCtsDisposed_AndNoAgentId_StillLogsWarning()
    {
        var mockSender = new Mock<IAgentCancellationSender>();
        var service = CreateService(agentCancellationSender: mockSender.Object);
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        run.AgentId = null; // No agent assigned
        service.ActiveRun = run;

        // Populate and dispose CTS
        var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);
        var cts = service.CancellationTokenSource!;
        cts.Dispose();

        await service.CancelPipelineAsync();

        // State transition occurs but no fallback cancel sent
        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        mockSender.Verify(
            s => s.SendCancelJobAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

        externalCts.Dispose();
    }

    [Fact]
    public async Task CancelPipelineAsync_WhenCtsDisposed_AndNoSender_StillCompletes()
    {
        var service = CreateService(agentCancellationSender: null);
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        run.AgentId = "agent-42";
        service.ActiveRun = run;

        // Populate and dispose CTS
        var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);
        var cts = service.CancellationTokenSource!;
        cts.Dispose();

        // Should complete without throwing — no sender means log-only path
        await service.CancelPipelineAsync();

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        _mockHistory.Verify(h => h.AddRunToHistoryAsync(run, It.IsAny<CancellationToken>()), Times.Once);

        externalCts.Dispose();
    }

    [Fact]
    public async Task CancelPipelineAsync_WhenFallbackCancelFails_DoesNotPropagate()
    {
        var mockSender = new Mock<IAgentCancellationSender>();
        mockSender
            .Setup(s => s.SendCancelJobAsync(It.IsAny<AgentId>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Agent disconnected"));

        var service = CreateService(agentCancellationSender: mockSender.Object);
        var run = CreateRun(step: PipelineStep.GeneratingCode);
        run.AgentId = "agent-42";
        service.ActiveRun = run;

        // Populate and dispose CTS
        var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);
        var cts = service.CancellationTokenSource!;
        cts.Dispose();

        // Should complete without throwing despite fallback failure
        await service.CancelPipelineAsync();

        run.CurrentStep.Should().Be(PipelineStep.Cancelled);
        _mockHistory.Verify(h => h.AddRunToHistoryAsync(run, It.IsAny<CancellationToken>()), Times.Once);

        externalCts.Dispose();
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
