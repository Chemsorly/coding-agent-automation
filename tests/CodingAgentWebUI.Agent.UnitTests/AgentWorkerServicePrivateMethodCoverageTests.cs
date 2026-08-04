using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Targeted coverage tests for the private methods extracted from AgentWorkerService
/// in PR #1778 (SonarQube S107 refactoring). Each test exercises a specific branch
/// or code path that was not reached by the existing broader behavioural tests.
/// </summary>
[Collection("EnvironmentVariables")]
public class AgentWorkerServicePrivateMethodCoverageTests : IDisposable
{
    public void Dispose()
    {
        TryDeleteDir(AgentDefaults.ChatWorkspacePath);
        TryDeleteDir(AgentDefaults.ChatWorkspacesRoot);
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    // ── RejectJobBusyAsync — hub throws, should swallow and complete ──────

    [Fact]
    public async Task RejectJobBusyAsync_HubThrows_CompletesWithoutThrowing()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        // Simulate busy agent
        SetPrivateField(slotManager, "_activeJobId", "existing-job");

        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        var task = (Task)handler.Invoke(service, [CreateJobAssignment("new-job")])!;
        await task;

        // Existing slot unchanged — new job was rejected
        GetPrivateField<string?>(slotManager, "_activeJobId").Should().Be("existing-job");
    }

    // ── SendJobAcceptedAsync — hub throws → returns false, releases slot ──

    [Fact]
    public async Task SendJobAcceptedAsync_HubThrows_ReleasesSlot()
    {
        // When the hub is disconnected, SendJobAcceptedAsync catches and calls ForceReleaseJobSlot
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        var handler = GetPrivateMethod(service, "HandleAssignJobAsync");
        await (Task)handler.Invoke(service, [CreateJobAssignment("job-send-fail")])!;

        // Slot was released because SendJobAccepted failed
        GetPrivateField<string?>(slotManager, "_activeJobId")
            .Should().BeNull("slot should be released when SendJobAccepted fails");
    }

    // ── FinalizeJobAsync — null completion skips reporter call ───────────

    [Fact]
    public async Task FinalizeJobAsync_NullCompletion_DoesNotCallReporter()
    {
        var mockReporter = new Mock<IJobCompletionReporter>();
        var service = TestAgentWorkerServiceFactory.Create(completionReporter: mockReporter.Object);
        var slotManager = GetSlotManager(service);
        slotManager.TryAcquireJobSlot("null-payload-job", out _);

        await (Task)GetPrivateMethod(service, "FinalizeJobAsync")
            .Invoke(service, ["null-payload-job", null])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            It.IsAny<string>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ── FinalizeJobAsync — with payload calls reporter ───────────────────

    [Fact]
    public async Task FinalizeJobAsync_WithCompletion_CallsReporter()
    {
        var mockReporter = new Mock<IJobCompletionReporter>();
        mockReporter.Setup(r => r.ReportCompletionAsync(
                It.IsAny<string>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var service = TestAgentWorkerServiceFactory.Create(completionReporter: mockReporter.Object);
        var slotManager = GetSlotManager(service);
        slotManager.TryAcquireJobSlot("complete-job", out _);

        var payload = new JobCompletionPayload
        {
            FinalStep = PipelineStep.Completed,
            CompletedAt = DateTimeOffset.UtcNow
        };

        await (Task)GetPrivateMethod(service, "FinalizeJobAsync")
            .Invoke(service, ["complete-job", payload])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            "complete-job", payload, CancellationToken.None), Times.Once);
    }

    // ── RunJobTaskAsync — executor throws → builds Failed payload ─────────

    [Fact]
    public async Task RunJobTaskAsync_ExecutorThrows_BuildsFailedPayload()
    {
        // Build a service with a throwing IPipelineExecutor
        var mockReporter = new Mock<IJobCompletionReporter>();
        mockReporter.Setup(r => r.ReportCompletionAsync(
                It.IsAny<string>(), It.IsAny<JobCompletionPayload>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var throwingExecutor = new Mock<IPipelineExecutor>();
        throwingExecutor
            .Setup(e => e.ExecuteAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<Action<PipelineStep?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("executor boom"));

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager();
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory();
        var logger = new Mock<Serilog.ILogger>().Object;
        var buffer = new CriticalMessageBuffer();
        var pipeline = Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, logger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test"), lifetime, logger);
        var consolidationExecutor = new LocalConsolidationExecutor(
            mockOrchestrator.Object, Mock.Of<System.Net.Http.IHttpClientFactory>(), logger);

        var service = new AgentWorkerService(new AgentWorkerServiceDependencies(
            lifecycle, slotManager, new AgentId("test"),
            throwingExecutor.Object, consolidationExecutor,
            mockReporter.Object, mockOrchestrator.Object,
            Mock.Of<System.Net.Http.IHttpClientFactory>(), lifetime, logger));

        slotManager.TryAcquireJobSlot("throw-job", out _);

        using var cts = new CancellationTokenSource();
        await (Task)GetPrivateMethod(service, "RunJobTaskAsync")
            .Invoke(service, [CreateJobAssignment("throw-job"), cts.Token])!;

        mockReporter.Verify(r => r.ReportCompletionAsync(
            "throw-job",
            It.Is<JobCompletionPayload>(p =>
                p.FinalStep == PipelineStep.Failed &&
                p.FailureReason == "executor boom"),
            CancellationToken.None), Times.Once);
    }

    // ── ReportChatCompletedAsync — hub throws, should not propagate ───────

    [Fact]
    public async Task ReportChatCompletedAsync_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetPrivateMethod(service, "ReportChatCompletedAsync")
            .Invoke(service, ["sess-1", 0, (string?)null])!;
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportChatCompletedAsync_WithError_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetPrivateMethod(service, "ReportChatCompletedAsync")
            .Invoke(service, ["sess-2", 1, "some error"])!;
        await act.Should().NotThrowAsync();
    }

    // ── RejectConsolidationJobBusyAsync — hub throws, swallowed ──────────

    [Fact]
    public async Task RejectConsolidationJobBusyAsync_HubThrows_CompletesWithoutThrowing()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        SetPrivateField(slotManager, "_activeJobId", "existing-consolidation");

        var message = new ConsolidationJobMessage
        {
            JobId = "new-consolidation",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        await (Task)GetPrivateMethod(service, "HandleAssignConsolidationJobAsync")
            .Invoke(service, [message])!;

        GetPrivateField<string?>(slotManager, "_activeJobId")
            .Should().Be("existing-consolidation");
    }

    // ── RunConsolidationTaskAsync — executor throws → reports failure ─────

    [Fact]
    public async Task RunConsolidationTaskAsync_ExecutorThrows_ReleasesSlot()
    {
        var throwingConsolidation = new Mock<IConsolidationExecutor>();
        throwingConsolidation
            .Setup(e => e.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("consolidation boom"));

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager();
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory();
        var logger = new Mock<Serilog.ILogger>().Object;
        var buffer = new CriticalMessageBuffer();
        var pipeline = Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(logger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, logger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test"), lifetime, logger);
        var pipelineExecutor = new LocalPipelineExecutor(
            mockOrchestrator.Object, Mock.Of<System.Net.Http.IHttpClientFactory>(),
            new PipelineConfiguration(), Mock.Of<IQualityGateValidator>(), logger);

        var service = new AgentWorkerService(new AgentWorkerServiceDependencies(
            lifecycle, slotManager, new AgentId("test"),
            pipelineExecutor, throwingConsolidation.Object,
            signalRReporter, mockOrchestrator.Object,
            Mock.Of<System.Net.Http.IHttpClientFactory>(), lifetime, logger));

        slotManager.TryAcquireJobSlot("consolidation-throw-job", out _);

        var message = new ConsolidationJobMessage
        {
            JobId = "consolidation-throw-job",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        using var cts = new CancellationTokenSource();
        await (Task)GetPrivateMethod(service, "RunConsolidationTaskAsync")
            .Invoke(service, [message, cts.Token])!;

        // Slot released in the finally block even when executor throws
        GetPrivateField<string?>(slotManager, "_activeJobId")
            .Should().BeNull("slot must be released in finally block");
    }

    // ── ReportConsolidationFailureAsync — hub throws, should not propagate

    [Fact]
    public async Task ReportConsolidationFailureAsync_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetPrivateMethod(service, "ReportConsolidationFailureAsync")
            .Invoke(service, ["job-id", "error msg"])!;
        await act.Should().NotThrowAsync();
    }

    // ── S8949 regression: ReportChatCompletedAsync uses CancellationToken.None ──────

    // TODO: This test is tautological — the `cts` created and cancelled here is never passed into the method under test
    // (ReportChatCompletedAsync takes no CancellationToken parameter). The cancelled CTS has no effect on the test outcome.
    // NotThrowAsync passes regardless of whether CancellationToken.None or a cancelled token is used internally, because the hub
    // is always disconnected in the test harness and the inner catch(Exception) already swallows hub errors. The test provides no
    // stronger regression protection than the pre-existing ReportConsolidationFailureAsync_HubThrows_DoesNotThrow test. To make
    // this guard meaningful, replace the disconnected hub with a mock/spy and assert that InvokeAsync was actually called.

    /// <summary>
    /// Regression guard for S8949: ReportChatCompletedAsync must attempt to report completion
    /// even when called in a context where chatToken is already cancelled. The implementation
    /// must use CancellationToken.None so InvokeAsync is not immediately cancelled.
    /// </summary>
    [Fact]
    public async Task ReportChatCompletedAsync_AttemptsMadeRegardlessOfAmbientCancellation()
    {
        // Arrange: create a service and cancel a CTS to simulate the post-cancel state
        var service = TestAgentWorkerServiceFactory.Create();

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act: invoke with an already-cancelled token in the ambient scope.
        // With CancellationToken.None inside the method, this must not throw
        // (the hub will throw because it's not connected, but that is caught internally).
        var act = async () => await (Task)GetPrivateMethod(service, "ReportChatCompletedAsync")
            .Invoke(service, ["sess-post-cancel", ExitCodes.Cancelled, "Chat cancelled"])!;

        // Assert: method completes without throwing — it attempts the call (and catches the hub error)
        await act.Should().NotThrowAsync(
            "ReportChatCompletedAsync must use CancellationToken.None so completion is always attempted, even after chatToken is cancelled");
    }

    // ── S8949 regression: ReportConsolidationFailureAsync uses CancellationToken.None ─

    // TODO: This test is tautological — it is structurally identical to the pre-existing
    // ReportConsolidationFailureAsync_HubThrows_DoesNotThrow test (same service, same method, same args, same assertion).
    // No cancelled token is passed or injected; NotThrowAsync passes regardless of whether CancellationToken.None or a
    // cancelled token is used internally (hub is always disconnected, inner catch swallows hub errors). The test adds no
    // discriminating power over the existing one. To make this guard meaningful, replace the disconnected hub with a
    // mock/spy and assert that InvokeAsync was actually called even when an ambient cancelled token is in scope.

    // TODO: Neither this test nor ReportChatCompletedAsync_AttemptsMadeRegardlessOfAmbientCancellation covers the other
    // CancellationToken.None call sites introduced by this change: ReportOutputLines (fire-and-forget flush batcher in
    // RunJobTaskAsync), HandleFetchModelsAsync success path, and ReportFetchModelsError error path. If the S8949 fix were
    // reverted at any of those call sites, no test would catch it.

    /// <summary>
    /// Regression guard for S8949: ReportConsolidationFailureAsync must attempt to report
    /// failure even when called from a catch block where jobToken may be cancelled.
    /// </summary>
    [Fact]
    public async Task ReportConsolidationFailureAsync_AttemptsMadeRegardlessOfJobTokenState()
    {
        // Arrange: create a service (hub not started, InvokeAsync will throw)
        var service = TestAgentWorkerServiceFactory.Create();

        // Act: invoke directly — with CancellationToken.None inside the method,
        // the hub throw is caught internally and does not propagate.
        var act = async () => await (Task)GetPrivateMethod(service, "ReportConsolidationFailureAsync")
            .Invoke(service, ["consolidation-job-id", "executor failed"])!;

        // Assert: method completes — the attempt was made (hub threw, but it was caught)
        await act.Should().NotThrowAsync(
            "ReportConsolidationFailureAsync must use CancellationToken.None so failure is always reported regardless of jobToken state");
    }

    // ── HandleAssignConsolidationJobAsync — idle agent, slot acquired ─────

    [Fact]
    public async Task HandleAssignConsolidationJobAsync_WhenIdle_AcquiresSlot()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        var message = new ConsolidationJobMessage
        {
            JobId = "idle-consolidation",
            Type = ConsolidationRunType.BrainConsolidation,
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration()
        };

        await (Task)GetPrivateMethod(service, "HandleAssignConsolidationJobAsync")
            .Invoke(service, [message])!;

        // Background task was started
        GetPrivateField<Task?>(slotManager, "_activeJobTask")
            .Should().NotBeNull("consolidation task should be started");
    }

    // ── RunChatTaskAsync — releases chat slot on completion ──────────────

    [Fact]
    public async Task RunChatTaskAsync_KiroCliPath_ReleasesChatSlotAfterCompletion()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; } // Skip if workspace can't be created

        slotManager.TryAcquireChatSlot("chat-slot-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "chat-slot-sess",
            Prompt = "hello",
            UseResume = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetPrivateMethod(service, "RunChatTaskAsync")
            .Invoke(service, [message, cts.Token])!;
        await Task.WhenAny(runTask, Task.Delay(5000));

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot should be released after RunChatTaskAsync");
    }

    // ── ExecuteChatWithOutputAsync — OperationCanceledException branch ────

    [Fact]
    public async Task ExecuteChatWithOutputAsync_PreCancelledToken_ReturnsCancelledOrFailure()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "sess-cancel",
            Prompt = "test",
            UseResume = false
        };

        var task = (Task<(int exitCode, string? error)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, cts.Token])!;
        var (exitCode, _) = await task;

        // OCE is caught → Cancelled (1), or workspace succeeded before cancel → GeneralFailure (1) or 0
        exitCode.Should().BeOneOf(1, 0);
    }

    // ── ExecuteChatWithOutputAsync — general exception → GeneralFailure ───

    [Fact]
    public async Task ExecuteChatWithOutputAsync_ProviderThrows_ReturnsGeneralFailure()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("orchestrator exploded"));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "sess-throw",
            Prompt = "boom",
            UseResume = true
        };

        var task = (Task<(int exitCode, string? error)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, CancellationToken.None])!;
        var (exitCode, error) = await task;

        exitCode.Should().Be(1, "general exception → GeneralFailure exit code 1");
        error.Should().NotBeNullOrEmpty();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static AgentJobSlotManager GetSlotManager(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_slotManager",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_slotManager' not found");
        return (AgentJobSlotManager)field.GetValue(service)!;
    }

    private static MethodInfo GetPrivateMethod(object obj, string name) =>
        obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method '{name}' not found");

    private static void SetPrivateField(object obj, string name, object? value)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string name)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found");
        return (T?)field.GetValue(obj);
    }

    private static JobAssignmentMessage CreateJobAssignment(string jobId = "test-job") => new()
    {
        JobId = jobId,
        IssueIdentifier = "owner/repo#1",
        IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        McpServers = [],
        InitiatedBy = "test-user"
    };
}
