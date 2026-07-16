using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.UnitTests.Properties;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Property-based tests for PipelineRunLifecycleService.
/// Feature: 017-pipeline-run-lifecycle-service
/// </summary>
public class PipelineRunLifecyclePropertyTests
{
    private static readonly PipelineStep[] TerminalSteps = [PipelineStep.Completed, PipelineStep.Failed, PipelineStep.Cancelled];

    private static PipelineRunLifecycleService CreateService(
        Mock<IPipelineRunHistoryService>? historyMock = null,
        Mock<IOrchestratorRunService>? runServiceMock = null)
    {
        var history = historyMock ?? new Mock<IPipelineRunHistoryService>();
        var runService = runServiceMock?.Object;
        var logger = new Mock<Serilog.ILogger>();
        return new PipelineRunLifecycleService(history.Object, runService, logger.Object);
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

    // ── Property 1: IsRunning Correctness ───────────────────────────────

    /// <summary>
    /// Property 1: IsRunning Correctness
    /// For any PipelineRun with any PipelineStep, IsRunning returns true iff
    /// ActiveRun is non-null AND CurrentStep is not Completed/Failed/Cancelled.
    /// **Validates: Requirements 1.1, 1.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool IsRunning_ReturnsTrue_IffActiveRunNonNullAndNonTerminal(LifecycleTestInput input)
    {
        var service = CreateService();

        if (input.HasActiveRun)
            service.ActiveRun = CreateRun(input.RunId, input.IssueId, input.Step);

        var expected = input.HasActiveRun && !TerminalSteps.Contains(input.Step);
        return service.IsRunning == expected;
    }

    // ── Property 2: HasAnyActiveRuns Aggregation ────────────────────────

    /// <summary>
    /// Property 2: HasAnyActiveRuns Aggregation
    /// For any combination of local run state and IOrchestratorRunService.HasActiveRuns,
    /// HasAnyActiveRuns returns true iff either IsRunning is true OR run service reports active runs.
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool HasAnyActiveRuns_CombinesLocalAndRunService(HasActiveRunsInput input)
    {
        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.HasActiveRuns).Returns(input.RunServiceHasActive);

        var service = CreateService(runServiceMock: runServiceMock);

        if (input.HasLocalRun)
            service.ActiveRun = CreateRun(step: input.Step);

        var localIsRunning = input.HasLocalRun && !TerminalSteps.Contains(input.Step);
        var expected = localIsRunning || input.RunServiceHasActive;
        return service.HasAnyActiveRuns == expected;
    }

    // ── Property 3: GetAllActiveRuns Union ──────────────────────────────

    /// <summary>
    /// Property 3: GetAllActiveRuns Union
    /// For any local active run and any set of agent-dispatched runs,
    /// GetAllActiveRuns returns exactly the local run (if active) plus all runs from the run service.
    /// **Validates: Requirements 1.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool GetAllActiveRuns_ReturnsUnionOfLocalAndAgentRuns(GetAllActiveRunsInput input)
    {
        var agentRuns = Enumerable.Range(0, input.AgentRunCount)
            .Select(i => CreateRun($"agent-{i}", $"agent-issue-{i}", PipelineStep.GeneratingCode))
            .ToList();

        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.GetActiveRuns()).Returns(agentRuns.AsReadOnly());

        var service = CreateService(runServiceMock: runServiceMock);

        if (input.HasLocalRun)
            service.ActiveRun = CreateRun(step: input.LocalStep);

        var result = service.GetAllActiveRuns();
        var expectedCount = (input.HasLocalRun ? 1 : 0) + input.AgentRunCount;
        return result.Count == expectedCount;
    }

    // ── Property 4: IsIssueBeingProcessed Dual-Source Check ─────────────

    /// <summary>
    /// Property 4: IsIssueBeingProcessed Dual-Source Check
    /// For any issue identifier, IsIssueBeingProcessed returns true iff local ActiveRun
    /// has that identifier and is running, OR IOrchestratorRunService.IsIssueBeingProcessed returns true.
    /// **Validates: Requirements 1.5**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool IsIssueBeingProcessed_ChecksBothLocalAndRunService(IsIssueBeingProcessedInput input)
    {
        var localIssueId = input.LocalMatchesQuery ? input.QueryIssue : "other-issue";

        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.IsIssueBeingProcessed(input.QueryIssue, It.IsAny<string>())).Returns(input.RunServiceReports);

        var service = CreateService(runServiceMock: runServiceMock);
        service.ActiveRun = CreateRun(issueId: localIssueId, step: input.LocalStep);

        var localIsRunning = !TerminalSteps.Contains(input.LocalStep);
        var localMatches = input.LocalMatchesQuery && localIsRunning;
        var expected = localMatches || input.RunServiceReports;

        return service.IsIssueBeingProcessed(input.QueryIssue, "ip-1") == expected;
    }

    // ── Property 5: Event Emission Correctness ──────────────────────────

    /// <summary>
    /// Property 5: Event Emission Correctness
    /// For any valid arguments, EmitOutputLine/NotifyChatResponse/NotifyChatCompleted
    /// invoke the corresponding event with those exact arguments.
    /// **Validates: Requirements 2.2, 2.3, 2.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool EventEmission_InvokesSubscribersWithExactArguments(EventEmissionInput input)
    {
        var service = CreateService();

        // Test OnOutputLine
        string? receivedLine = null;
        service.OnOutputLine += line => receivedLine = line;
        service.EmitOutputLine(input.OutputLine);
        var outputCorrect = receivedLine == input.OutputLine;

        // Test OnChatResponse
        string? receivedSessionId = null;
        IReadOnlyList<string>? receivedLines = null;
        var chatLines = new List<string> { "line1", "line2" }.AsReadOnly();
        service.OnChatResponse += (sid, lines) => { receivedSessionId = sid; receivedLines = lines; };
        service.NotifyChatResponse(input.SessionId, chatLines);
        var chatResponseCorrect = receivedSessionId == input.SessionId && receivedLines == chatLines;

        // Test OnChatCompleted
        string? completedSessionId = null;
        int? completedExitCode = null;
        string? completedError = null;
        service.OnChatCompleted += (sid, code, err) => { completedSessionId = sid; completedExitCode = code; completedError = err; };
        service.NotifyChatCompleted(input.SessionId, input.ExitCode, "test error");
        var chatCompletedCorrect = completedSessionId == input.SessionId
            && completedExitCode == input.ExitCode
            && completedError == "test error";

        return outputCorrect && chatResponseCorrect && chatCompletedCorrect;
    }

    // ── Property 6: Event Exception Isolation ───────────────────────────

    /// <summary>
    /// Property 6: Event Exception Isolation
    /// For any subscriber that throws, calling NotifyChange/EmitOutputLine/NotifyChatResponse/NotifyChatCompleted
    /// does NOT propagate the exception.
    /// **Validates: Requirements 2.5, 2.6, 2.7**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool EventExceptionIsolation_DoesNotPropagate(EventEmissionInput input)
    {
        var service = CreateService();

        // Subscribe throwing handlers
        service.OnChange += () => throw new InvalidOperationException("OnChange boom");
        service.OnOutputLine += _ => throw new InvalidOperationException("OnOutputLine boom");
        service.OnChatResponse += (_, _) => throw new InvalidOperationException("OnChatResponse boom");
        service.OnChatCompleted += (_, _, _) => throw new InvalidOperationException("OnChatCompleted boom");

        // None of these should throw
        try
        {
            service.NotifyChange();
            service.EmitOutputLine(input.OutputLine);
            service.NotifyChatResponse(input.SessionId, new List<string> { "line" }.AsReadOnly());
            service.NotifyChatCompleted(input.SessionId, input.ExitCode, null);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Property 7: TransitionTo Postconditions ─────────────────────────

    /// <summary>
    /// Property 7: TransitionTo Postconditions
    /// For any PipelineRun and PipelineStep, after TransitionTo:
    /// CurrentStep equals new step, HighWaterMark updated if non-terminal and exceeds current
    /// (using StepOrder logical ordering, not enum ordinals), OnChange invoked.
    /// **Validates: Requirements 3.1, 3.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool TransitionTo_UpdatesStateCorrectly(TransitionInput input)
    {
        var service = CreateService();
        var run = CreateRun(step: input.InitialStep);
        var initialHighWaterMark = run.HighWaterMark;
        var onChangeFired = false;
        service.OnChange += () => onChangeFired = true;

        service.TransitionTo(run, input.TargetStep);

        // (a) CurrentStep equals new step
        var stepCorrect = run.CurrentStep == input.TargetStep;

        // (b) HighWaterMark updated if step is not Failed/Cancelled and exceeds current
        // Uses StepOrder.GetOrder (logical execution order) — NOT enum ordinals.
        // Steps from different pipeline types may have overlapping orders; the production
        // code uses StepOrder for comparison and so must this assertion.
        bool hwmCorrect;
        var hwmExcluded = input.TargetStep is PipelineStep.Failed or PipelineStep.Cancelled;
        if (!hwmExcluded && StepOrder.GetOrder(input.TargetStep) > StepOrder.GetOrder(initialHighWaterMark))
            hwmCorrect = run.HighWaterMark == input.TargetStep;
        else
            hwmCorrect = run.HighWaterMark == initialHighWaterMark;

        // (c) OnChange invoked
        return stepCorrect && hwmCorrect && onChangeFired;
    }

    // ── Property 8: FailRunAsync Postconditions ─────────────────────────

    /// <summary>
    /// Property 8: FailRunAsync Postconditions
    /// For any PipelineRun and failure reason, after FailRunAsync:
    /// FailureReason set, CompletedAt non-null, CurrentStep is Failed, run added to history.
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool FailRunAsync_SetsFailureStateCorrectly(FailRunInput input)
    {
        var historyMock = new Mock<IPipelineRunHistoryService>();
        var addedRuns = new List<PipelineRun>();
        historyMock.Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback<PipelineRun, CancellationToken>((r, _) => addedRuns.Add(r));

        var service = CreateService(historyMock: historyMock);
        var run = CreateRun(input.RunId, step: input.Step);

        service.FailRunAsync(run, input.Reason).GetAwaiter().GetResult();

        var reasonCorrect = run.FailureReason == input.Reason;
        var completedAtSet = run.CompletedAt != null;
        var stepIsFailed = run.CurrentStep == PipelineStep.Failed;
        var addedToHistory = addedRuns.Contains(run);

        return reasonCorrect && completedAtSet && stepIsFailed && addedToHistory;
    }

    // ── Property 9: CancelPipelineAsync Postconditions ──────────────────

    /// <summary>
    /// Property 9: CancelPipelineAsync Postconditions
    /// For any active PipelineRun, after CancelPipelineAsync:
    /// CTS cancelled, CompletedAt non-null, CurrentStep is Cancelled, run added to history.
    /// **Validates: Requirements 4.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool CancelPipelineAsync_CancelsAndTransitions(CancelRunInput input)
    {
        var historyMock = new Mock<IPipelineRunHistoryService>();
        var addedRuns = new List<PipelineRun>();
        historyMock.Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback<PipelineRun, CancellationToken>((r, _) => addedRuns.Add(r));

        var service = CreateService(historyMock: historyMock);
        var run = CreateRun(input.RunId, step: input.Step);
        service.ActiveRun = run;

        // Create a linked token so CTS exists
        using var externalCts = new CancellationTokenSource();
        service.CreateLinkedCancellationToken(externalCts.Token);

        service.CancelPipelineAsync().GetAwaiter().GetResult();

        var ctsCancelled = service.CancellationTokenSource?.IsCancellationRequested == true;
        var completedAtSet = run.CompletedAt != null;
        var stepIsCancelled = run.CurrentStep == PipelineStep.Cancelled;
        var addedToHistory = addedRuns.Contains(run);

        return ctsCancelled && completedAtSet && stepIsCancelled && addedToHistory;
    }

    // ── Property 10: MarkAgentRunsCancelled Postconditions ──────────────

    /// <summary>
    /// Property 10: MarkAgentRunsCancelled Postconditions
    /// For any set of active agent-dispatched runs, after MarkAgentRunsCancelled:
    /// every run has CompletedAt set, CurrentStep is Cancelled, added to history.
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool MarkAgentRunsCancelled_CancelsAllAgentRuns(MarkAgentRunsCancelledInput input)
    {
        var agentRuns = Enumerable.Range(0, input.AgentRunCount)
            .Select(i => CreateRun($"agent-{i}", $"issue-{i}", PipelineStep.GeneratingCode))
            .ToList();

        var historyMock = new Mock<IPipelineRunHistoryService>();
        var addedRuns = new List<PipelineRun>();
        historyMock.Setup(h => h.AddRunToHistoryAsync(It.IsAny<PipelineRun>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask).Callback<PipelineRun, CancellationToken>((r, _) => addedRuns.Add(r));

        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.GetActiveRuns()).Returns(agentRuns.AsReadOnly());

        var removedRunIds = new List<string>();
        runServiceMock.Setup(r => r.RemoveRun(It.IsAny<string>()))
            .Callback<string>(id => removedRunIds.Add(id));

        var service = CreateService(historyMock: historyMock, runServiceMock: runServiceMock);

        var cancelledIssues = service.MarkAgentRunsCancelled().GetAwaiter().GetResult();

        var allCompleted = agentRuns.All(r => r.CompletedAt != null);
        var allCancelled = agentRuns.All(r => r.CurrentStep == PipelineStep.Cancelled);
        var allInHistory = agentRuns.All(r => addedRuns.Contains(r));
        var allRemoved = agentRuns.All(r => removedRunIds.Contains(r.RunId));
        var returnedIssues = cancelledIssues.SequenceEqual(agentRuns.Select(r => (r.IssueIdentifier, r.IssueProviderConfigId)));

        return allCompleted && allCancelled && allInHistory && allRemoved && returnedIssues;
    }

    // ── Property 11: RegisterDispatchedRun Success ──────────────────────

    /// <summary>
    /// Property 11: RegisterDispatchedRun Success
    /// For any PipelineRun whose IssueIdentifier is NOT already being processed,
    /// RegisterDispatchedRun returns true, adds to run service, fires OnChange.
    /// **Validates: Requirements 5.1**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool RegisterDispatchedRun_WhenNotProcessing_ReturnsTrue(RegisterRunInput input)
    {
        var run = CreateRun(input.RunId, input.IssueId, PipelineStep.GeneratingCode);

        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.IsIssueBeingProcessed(input.IssueId, It.IsAny<string>())).Returns(false);

        var service = CreateService(runServiceMock: runServiceMock);

        var onChangeFired = false;
        service.OnChange += () => onChangeFired = true;

        var result = service.RegisterDispatchedRun(run);

        runServiceMock.Verify(r => r.AddRun(run), Times.Once);
        return result && onChangeFired;
    }

    // ── Property 12: RegisterDispatchedRun Guard ────────────────────────

    /// <summary>
    /// Property 12: RegisterDispatchedRun Guard
    /// For any PipelineRun whose IssueIdentifier IS already being processed,
    /// RegisterDispatchedRun returns false and does NOT add to run service.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineRunLifecycleArbitraries) })]
    public bool RegisterDispatchedRun_WhenAlreadyProcessing_ReturnsFalse(RegisterRunInput input)
    {
        var run = CreateRun(input.RunId, input.IssueId, PipelineStep.GeneratingCode);

        var runServiceMock = new Mock<IOrchestratorRunService>();
        runServiceMock.Setup(r => r.IsIssueBeingProcessed(input.IssueId, It.IsAny<string>())).Returns(true);

        var service = CreateService(runServiceMock: runServiceMock);

        var result = service.RegisterDispatchedRun(run);

        runServiceMock.Verify(r => r.AddRun(It.IsAny<PipelineRun>()), Times.Never);
        return !result;
    }

    // ── Property 13: CreateLinkedCancellationToken Linking ───────────────

    /// <summary>
    /// Property 13: CreateLinkedCancellationToken Linking
    /// For any CancellationToken, the token returned by CreateLinkedCancellationToken
    /// is cancelled when the external token is cancelled.
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public bool CreateLinkedCancellationToken_CancelsWhenExternalCancels(bool cancelExternal)
    {
        var service = CreateService();
        using var externalCts = new CancellationTokenSource();

        var linkedToken = service.CreateLinkedCancellationToken(externalCts.Token);

        if (cancelExternal)
            externalCts.Cancel();

        var result = linkedToken.IsCancellationRequested == cancelExternal;
        service.Dispose();
        return result;
    }
}
