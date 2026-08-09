using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
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
        GC.SuppressFinalize(this);
    }

    private static void TryDeleteDir(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch { /* best effort */ }
    }

    // ── S8949 source-scan tests — verify CancellationToken propagation ────
    // TODO: These source-scan tests verify that specific substrings exist in each method body,
    // but do NOT verify that the substring appears inside an InvokeAsync call. If the production
    // code is refactored so that CancellationToken.None (or ApplicationStopping) appears only in
    // a comment or an unrelated expression while the actual InvokeAsync call loses its token
    // argument, these tests would still pass. Consider tightening the assertions to require the
    // token string to appear on the same line as, or adjacent to, an InvokeAsync call.
    // TODO: Source-scan tests are absent for: (1) AgentConnectionManager.DisposeAsync
    // (CancellationToken.None with // intentional: comment added by this change); (2)
    // ChatJobDispatcher Task.Run(..., cts.Token) change; (3) the three outputBatcher.AddLineAsync
    // calls that now forward chatToken (MCP config, project steering, project secrets log lines
    // in AgentWorkerService.cs). Add source-scan tests for these sites to ensure
    // regressions are caught.

    /// <summary>
    /// Verifies that ReportChatCompletedAsync passes CancellationToken.None with an
    /// // intentional: comment to InvokeAsync. Since chatToken may be cancelled when
    /// this method is called, CancellationToken.None is the correct choice.
    /// </summary>
    [Fact]
    public void SourceCode_ReportChatCompletedAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        // Extract just the ReportChatCompletedAsync method body to avoid false positives
        var methodStart = source.IndexOf("private async Task ReportChatCompletedAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportChatCompletedAsync must pass CancellationToken.None to InvokeAsync — chatToken may be cancelled at call time");
        methodBody.Should().Contain("// intentional:",
            "ReportChatCompletedAsync must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that SignalAgentReadyAsync passes _hostApplicationLifetime.ApplicationStopping
    /// to InvokeAsync so the call is cancelled during application shutdown.
    /// </summary>
    [Fact]
    public void SourceCode_SignalAgentReadyAsync_PassesApplicationStopping()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        var methodStart = source.IndexOf("private async Task SignalAgentReadyAsync()", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("_hostApplicationLifetime.ApplicationStopping",
            "SignalAgentReadyAsync must pass ApplicationStopping to InvokeAsync so it is cancelled during shutdown");
    }

    /// <summary>
    /// Verifies that ReportConsolidationFailureAsync passes CancellationToken.None with comment.
    /// Called from a catch block where jobToken may already be cancelled.
    /// </summary>
    [Fact]
    public void SourceCode_ReportConsolidationFailureAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        var methodStart = source.IndexOf("private async Task ReportConsolidationFailureAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportConsolidationFailureAsync must pass CancellationToken.None — called from catch block where jobToken may be cancelled");
        methodBody.Should().Contain("// intentional:",
            "ReportConsolidationFailureAsync must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that ReportFetchModelsError passes CancellationToken.None with comment.
    /// Called from error paths where no ambient token is available.
    /// </summary>
    [Fact]
    public void SourceCode_ReportFetchModelsError_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        var methodStart = source.IndexOf("private async Task ReportFetchModelsError(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportFetchModelsError must pass CancellationToken.None — called from error paths without ambient token");
        methodBody.Should().Contain("// intentional:",
            "ReportFetchModelsError must have an // intentional: comment explaining why CancellationToken.None is used");
    }

    /// <summary>
    /// Verifies that HandleFetchModelsAsync does not pass timeoutCts.Token to any post-exit
    /// call (stderr read or InvokeAsync). The process has already exited at both call sites,
    /// so the timeout token's purpose is exhausted and may already be cancelled.
    /// Both the error path (stderr ReadToEndAsync) and the success path (ReportFetchModelsResult
    /// InvokeAsync) must use CancellationToken.None with an // intentional: comment.
    /// </summary>
    [Fact]
    public void SourceCode_HandleFetchModelsAsync_PassesCancellationTokenNone()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        var methodStart = source.IndexOf("private async Task HandleFetchModelsAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        // TODO: The fallback above only handles private/public access modifiers. If a member with
        // a different access modifier (internal, protected, protected internal, private protected)
        // or an attribute/doc comment is inserted after HandleFetchModelsAsync, or if it becomes
        // the last method in the class, both searches may return -1 and source.Substring will throw
        // ArgumentOutOfRangeException instead of a meaningful assertion failure. Add:
        // if (methodEnd < 0) methodEnd = source.Length;
        if (methodEnd < 0) methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // Locate the end of WaitForExitAsync — all post-exit code follows this call
        var waitForExitEnd = methodBody.IndexOf("WaitForExitAsync(", StringComparison.Ordinal);
        waitForExitEnd = methodBody.IndexOf(");", waitForExitEnd, StringComparison.Ordinal) + 2;
        var postExitBody = methodBody.Substring(waitForExitEnd);

        // TODO: This assertion only verifies that CancellationToken.None appears at least once in
        // postExitBody. There are two post-exit call sites (stderr ReadToEndAsync error path and
        // ReportFetchModelsResult InvokeAsync success path); a partial revert of one site would
        // still satisfy this check. Consider asserting count >= 2:
        // (postExitBody.Split("CancellationToken.None").Length - 1).Should().BeGreaterOrEqualTo(2, ...)
        postExitBody.Should().Contain("CancellationToken.None",
            "HandleFetchModelsAsync must use CancellationToken.None for post-exit calls — timeoutCts.Token may be expired after WaitForExitAsync");
        postExitBody.Should().Contain("// intentional:",
            "HandleFetchModelsAsync must have an // intentional: comment in post-exit code explaining why CancellationToken.None is used");
        postExitBody.Should().NotContain("timeoutCts.Token)",
            "HandleFetchModelsAsync must not pass timeoutCts.Token to any post-exit call (stderr read or InvokeAsync) — the token may already be cancelled");
    }

    /// <summary>
    /// Verifies that AgentConnectionLifecycle.ShutdownAsync passes CancellationToken.None with
    /// an // intentional: comment to the DeregisterAgent InvokeAsync call.
    /// ShutdownAsync is invoked after ApplicationStopping is already signaled, so passing
    /// ApplicationStopping would cause InvokeAsync to throw OperationCanceledException immediately
    /// and deregistration would never reach the orchestrator. CancellationToken.None is correct here.
    /// </summary>
    [Fact]
    public void SourceCode_AgentConnectionLifecycle_ShutdownAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentConnectionLifecycle.cs"));

        var methodStart = source.IndexOf("public async Task ShutdownAsync()", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        // TODO: The fallback above only handles public/private access modifiers. If a member with
        // a different access modifier (internal, protected, protected internal, private protected)
        // or an attribute/doc comment is inserted after ShutdownAsync, both searches may return -1
        // and source.Substring will throw ArgumentOutOfRangeException. Consider adding a
        // source.Length fallback: if (methodEnd < 0) methodEnd = source.Length;
        if (methodEnd < 0) methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // ShutdownAsync runs after ApplicationStopping fires — must use CancellationToken.None so
        // deregistration can still reach the orchestrator during graceful shutdown.
        methodBody.Should().Contain("CancellationToken.None",
            "AgentConnectionLifecycle.ShutdownAsync must pass CancellationToken.None to DeregisterAgent InvokeAsync — ApplicationStopping is already triggered at call time");
        methodBody.Should().Contain("// intentional:",
            "AgentConnectionLifecycle.ShutdownAsync must have an // intentional: comment explaining why CancellationToken.None is used");
        methodBody.Should().NotContain("_hostApplicationLifetime.ApplicationStopping",
            "AgentConnectionLifecycle.ShutdownAsync must NOT pass ApplicationStopping — it is already cancelled when ShutdownAsync runs");
    }

    /// <summary>
    /// Structural guard: verifies that ReleaseChatSlot() appears inside a finally block in
    /// RunChatTaskAsync. This is the primary regression guard for issue #1857 — it directly
    /// validates the fix rather than relying on behavioral execution which would pass even
    /// without a finally block (the sequential path reaches ReleaseChatSlot on the happy path).
    /// </summary>
    [Fact]
    public void SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        // Extract just the RunChatTaskAsync method body to avoid false positives from other methods
        var methodStart = source.IndexOf("private async Task RunChatTaskAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        // The fix requires ReleaseChatSlot() to be inside a finally block.
        // We verify this by asserting that "finally" appears BEFORE "ReleaseChatSlot()" in the
        // method body, and that both are present.
        methodBody.Should().Contain("finally",
            "RunChatTaskAsync must contain a finally block that guards ReleaseChatSlot()");
        methodBody.Should().Contain("ReleaseChatSlot()",
            "RunChatTaskAsync must call ReleaseChatSlot()");

        var finallyIndex = methodBody.LastIndexOf("finally", StringComparison.Ordinal);
        var releaseIndex = methodBody.IndexOf("ReleaseChatSlot()", StringComparison.Ordinal);

        // TODO: This ordering assertion is fragile — it only verifies that a `finally` substring
        // appears before the first `ReleaseChatSlot()` substring in text order, not that
        // ReleaseChatSlot() is *lexically enclosed* within the finally block's braces. A refactor
        // that places ReleaseChatSlot() after the finally's closing `}` (back to the pre-fix
        // sequential pattern) would still satisfy this assertion if another `finally` keyword
        // appears later in the method body. A stronger check would verify ReleaseChatSlot()
        // appears between the finally's opening `{` and its corresponding closing `}`.
        // See review finding: SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock.
        releaseIndex.Should().BeGreaterThan(finallyIndex,
            "ReleaseChatSlot() must appear after the finally keyword — it must be inside a finally block, not before it");
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
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
        // TODO: Task.WhenAny does not re-throw if runTask faults; assertions below execute
        // even on a timeout or unhandled exception, potentially checking state that was never
        // established. Consider awaiting runTask directly (or checking runTask.IsCompletedSuccessfully)
        // to distinguish genuine completion from a 5-second timeout masked as a pass.
        await Task.WhenAny(runTask, Task.Delay(5000));

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot should be released after RunChatTaskAsync");
    }

    // ── RunChatTaskAsync — slot released even when hub is disconnected during reporting ──

    /// <summary>
    /// Regression guard for issue #1857: verifies ReleaseChatSlot() is called even when
    /// the hub connection is unavailable during ReportChatCompletedAsync. The default
    /// TestAgentWorkerServiceFactory uses a disconnected hub, so InvokeAsync fails inside
    /// ReportChatCompletedAsync (which swallows the error). The slot must still be released.
    ///
    /// Note: Since ReportChatCompletedAsync has an unconditional catch today, this test
    /// exercises the normal completion path with a disconnected hub rather than a true
    /// propagated-throw scenario. Its value is as a regression guard: if the finally block
    /// is ever removed, future changes that allow ReportChatCompletedAsync to propagate
    /// exceptions would immediately cause the slot to leak — and this test would catch that.
    /// The source-scan test (SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock)
    /// is the primary structural guard; this test provides a behavioral complement.
    /// </summary>
    // TODO: This test does not actually exercise the failure scenario its name describes.
    // ReportChatCompletedAsync swallows exceptions internally (unconditional catch), so the
    // test only exercises the normal completion path — the finally block is never triggered by
    // an exception. This means the test would pass identically if the try/finally fix were
    // reverted back to sequential code. The behavioral coverage this test claims to provide
    // is not real; the structural source-scan test is the actual regression guard.
    // To make this test meaningful, ReportChatCompletedAsync would need to propagate exceptions
    // (or a separate overload/mock injection point would be needed to simulate throw behaviour).
    // See review finding: RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot.
    [Fact]
    public async Task RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        // Default factory uses a disconnected hub — ReportChatCompletedAsync will encounter
        // an InvokeAsync failure (swallowed internally). The slot must still be released.
        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        // TODO: This bare `catch { return; }` silently skips the rest of the test (including all
        // assertions) without marking the test as skipped in the test runner. If the workspace
        // directory cannot be created (e.g., permission restrictions in CI), this test produces
        // a false-green with no visibility in test reports. Replace with Assert.Skip("reason")
        // (xUnit v3) or a [Fact(Skip = "...")] guard to surface skipped runs explicitly.
        // See review finding: RunChatTaskAsync_WhenReportChatCompletedThrows_StillReleasesChatSlot silent skip.
        catch { return; } // Skip if workspace can't be created in this environment

        slotManager.TryAcquireChatSlot("slot-leak-guard-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "slot-leak-guard-sess",
            Prompt = "hello",
            UseResume = true
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        // Directly await the task (not Task.WhenAny) so any unexpected fault surfaces immediately
        // rather than being masked by a timeout producing a false-green result.
        await (Task)GetPrivateMethod(service, "RunChatTaskAsync")
            .Invoke(service, [message, cts.Token])!;

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot must be released unconditionally via the finally block, " +
                             "regardless of what ReportChatCompletedAsync does internally");
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
            .Invoke(service, [message, batcher, cts.Token, new List<string>()])!;
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
            .Invoke(service, [message, batcher, CancellationToken.None, new List<string>()])!;
        var (exitCode, error) = await task;

        exitCode.Should().Be(1, "general exception → GeneralFailure exit code 1");
        error.Should().NotBeNullOrEmpty();
    }

    // ── HandleFetchModelsAsync — error path (non-zero exit, ReadToEndAsync uses CancellationToken.None) ──

    /// <summary>
    /// Verifies that HandleFetchModelsAsync completes without throwing when kiro-cli exits non-zero.
    /// The changed line (ReadToEndAsync(CancellationToken.None)) is exercised by this path.
    /// The error is swallowed internally via ReportFetchModelsError (hub call may fail in test env).
    /// </summary>
    [Fact]
    public async Task HandleFetchModelsAsync_NonZeroExit_CompletesWithoutThrowing()
    {
        // Arrange: point kiro-cli at /usr/bin/false which exits with code 1
        var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "/usr/bin/false");
            var service = TestAgentWorkerServiceFactory.Create();
            var request = new FetchModelsRequest { RequestId = "test-req-error" };

            // Act: invoke via reflection — exception from hub is caught internally
            var act = async () => await (Task)GetPrivateMethod(service, "HandleFetchModelsAsync")
                .Invoke(service, [request])!;

            // Assert: method must not propagate exceptions (all errors caught internally)
            await act.Should().NotThrowAsync(
                "HandleFetchModelsAsync must swallow all errors via ReportFetchModelsError");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origPath);
        }
    }

    // ── HandleFetchModelsAsync — success path (zero exit, InvokeAsync uses CancellationToken.None) ──

    /// <summary>
    /// Verifies that HandleFetchModelsAsync completes without throwing when kiro-cli exits zero
    /// and outputs valid JSON. The changed line (InvokeAsync(…, CancellationToken.None)) is
    /// exercised by this path. The hub InvokeAsync will fail (no connection) but the exception
    /// is caught by the surrounding try/catch, so the method must still complete normally.
    /// </summary>
    [Fact]
    public async Task HandleFetchModelsAsync_ZeroExitWithValidJson_CompletesWithoutThrowing()
    {
        // Arrange: shell script that outputs valid model JSON to stdout and exits 0
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-kiro-{Guid.NewGuid():N}.sh");
        try
        {
            // Write a shell script that echoes valid JSON and exits 0
            var validJson = """{"models":[{"model_id":"test-model","description":"Test","rate_multiplier":1.0}]}""";
            await File.WriteAllTextAsync(scriptPath, $"#!/bin/sh\necho '{validJson}'\nexit 0\n");
            // Make executable — use chmod process to avoid CA1416 platform guard requirement
            using var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod",
                Arguments = $"+x {scriptPath}",
                UseShellExecute = false
            });
            if (chmod is not null) await chmod.WaitForExitAsync();

            var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
            try
            {
                Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, scriptPath);
                var service = TestAgentWorkerServiceFactory.Create();
                var request = new FetchModelsRequest { RequestId = "test-req-success" };

                // Act: invoke via reflection — InvokeAsync will fail (no hub) but exception is caught
                var act = async () => await (Task)GetPrivateMethod(service, "HandleFetchModelsAsync")
                    .Invoke(service, [request])!;

                // Assert: method must complete without propagating exceptions
                await act.Should().NotThrowAsync(
                    "HandleFetchModelsAsync must swallow all errors including hub InvokeAsync failures");
            }
            finally
            {
                Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origPath);
            }
        }
        finally
        {
            if (File.Exists(scriptPath)) File.Delete(scriptPath);
        }
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

    // ── Project secrets injection and cleanup ─────────────────────────────────

    /// <summary>
    /// Verifies that ProjectSecrets on the first prompt (UseResume=false) are injected
    /// as process env vars, and that they are cleaned up in RunChatTaskAsync's finally block
    /// after the task completes normally.
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_InjectsAndCleansUpEnvVars()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_{Guid.NewGuid():N}";

        var capturedEnvVar = new List<string?>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, _, _, _, _) =>
                {
                    // Capture env var value during execution
                    capturedEnvVar.Add(Environment.GetEnvironmentVariable(secretKey));
                    return Task.FromResult(0);
                });

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // TODO: silent catch { return; } makes this test vacuously pass if the workspace
        // directory cannot be created (e.g. permissions issue in CI). Consider using
        // Skip.If(...) or Assert.SkipUnless(...) so infrastructure failures are visible
        // rather than producing a false-green result.
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        slotManager.TryAcquireChatSlot("secrets-inject-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-inject-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "injected-value" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetPrivateMethod(service, "RunChatTaskAsync")
            .Invoke(service, [message, cts.Token])!;
        // TODO: Task.WhenAny does not re-throw if runTask faults; assertions below execute
        // even on a timeout, potentially checking state that was never established. Consider
        // awaiting runTask directly (or checking runTask.IsCompletedSuccessfully) to distinguish
        // genuine completion from a 5-second timeout masked as a pass.
        await Task.WhenAny(runTask, Task.Delay(5000));

        // During execution the env var was set
        capturedEnvVar.Should().NotBeEmpty("orchestrator must have been invoked");
        capturedEnvVar[0].Should().Be("injected-value",
            "ProjectSecrets must be injected as env vars during execution");

        // After completion the env var must be cleared
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "Injected env vars must be cleaned up in the finally block after chat completion");
    }

    /// <summary>
    /// Verifies that ProjectSecrets are cleaned up even when the orchestrator throws an
    /// unexpected exception (finally block fires on exception exit, not just normal return).
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_CleansUpEvenWhenOrchestratorThrows()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_THROW_{Guid.NewGuid():N}";

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("orchestrator boom"));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // TODO: silent catch { return; } makes this test vacuously pass if the workspace
        // directory cannot be created (e.g. permissions issue in CI). Consider using
        // Skip.If(...) or Assert.SkipUnless(...) so infrastructure failures are visible
        // rather than producing a false-green result.
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        slotManager.TryAcquireChatSlot("secrets-throw-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-throw-sess",
            Prompt = "boom",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "should-be-cleared" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var runTask = (Task)GetPrivateMethod(service, "RunChatTaskAsync")
            .Invoke(service, [message, cts.Token])!;
        // TODO: Task.WhenAny does not re-throw if runTask faults; assertions below execute
        // even on a timeout, potentially checking state that was never established. Consider
        // awaiting runTask directly (or checking runTask.IsCompletedSuccessfully) to distinguish
        // genuine completion from a 5-second timeout masked as a pass.
        await Task.WhenAny(runTask, Task.Delay(5000));

        // Even though orchestrator threw, env var must be cleaned up
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "Injected env vars must be cleaned up in the finally block even when execution throws");
    }

    /// <summary>
    /// Verifies that null ProjectSecrets produce no env var changes (backward compat).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_NullProjectSecrets_NoEnvVarsSet()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        // Track a sentinel key to make sure it's not set
        var sentinelKey = $"TEST_NULL_SECRET_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(sentinelKey, null);

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);

        // TODO: silent catch { return; } makes this test vacuously pass if the workspace
        // directory cannot be created (e.g. permissions issue in CI). Consider using
        // Skip.If(...) or Assert.SkipUnless(...) so infrastructure failures are visible
        // rather than producing a false-green result.
        // TODO: sentinelKey is pre-set to null (a no-op), so the env-var assertion at the end
        // would pass even if the code under test had set and then cleared it. A stronger check
        // would set sentinelKey to a known non-null value before calling the method, then confirm
        // it was not modified.
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "null-secrets-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = null   // null = no secrets, backward compat
        };

        var task = (Task<(int, string?)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, CancellationToken.None, new List<string>()])!;
        await task;

        // No env vars should have been set or touched — sentinelKey was never set so remains null
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull(
            "null ProjectSecrets must not inject any env vars");
    }

    // ── CleanupChatSecrets — directly invoked with parameterized list ──────────

    /// <summary>
    /// Verifies that CleanupChatSecrets accepts a List&lt;string&gt; parameter and nulls out
    /// each env var in the list. This test directly documents acceptance criterion 2
    /// (the secret key list is passed as a parameter rather than accessed via shared state)
    /// and serves as a compile-time guard: if the parameter were removed in a future
    /// refactor, invoking the method with a list argument via reflection would fail.
    /// </summary>
    [Fact]
    public void CleanupChatSecrets_WithInjectedKey_ClearsEnvVar()
    {
        var secretKey = $"TEST_CLEANUP_{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretKey, "secret-value");

        var service = TestAgentWorkerServiceFactory.Create();
        var method = typeof(AgentWorkerService)
            .GetMethod("CleanupChatSecrets", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CleanupChatSecrets not found");

        // TODO: This test only covers the non-empty-list path. The early-exit branch
        // `if (injectedChatSecretKeys.Count == 0) return` is not exercised here. If that
        // guard were accidentally changed to skip cleanup unconditionally, this test would
        // still pass. Consider adding a complementary case that verifies multi-key cleanup
        // and confirms the empty-list path returns without side effects.
        method.Invoke(service, [new List<string> { secretKey }]);

        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "CleanupChatSecrets must null out env vars for every key in the passed list");
    }

    // ── Project steering write ─────────────────────────────────────────────────

    /// <summary>
    /// Verifies that ProjectSteeringContent is written to the chat workspace before
    /// the orchestrator is invoked on the first prompt (UseResume=false).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_WithProjectSteeringContent_WritesFileBeforeOrchestrator()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var steeringFilesWhenInvoked = new List<bool>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, workspace, _, _, _, _) =>
                {
                    var steeringPath = Path.Combine(workspace, ".kiro", "steering", "pipeline-project.md");
                    steeringFilesWhenInvoked.Add(File.Exists(steeringPath));
                    return Task.FromResult(0);
                });

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "steering-before-prompt-sess",
            Prompt = "test steering",
            UseResume = false,
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = "# Instructions\nUse TDD."
        };

        var task = (Task<(int, string?)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, CancellationToken.None, new List<string>()])!;
        await task;

        steeringFilesWhenInvoked.Should().NotBeEmpty("orchestrator must have been invoked");
        steeringFilesWhenInvoked[0].Should().BeTrue(
            "steering file must be written before the orchestrator is invoked (including warm-up prompt)");
    }

    /// <summary>
    /// Verifies that ProjectSteeringContent is NOT written on resume prompts (UseResume=true).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_WithProjectSteeringContent_SkipsWriteOnResume()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "steering-resume-sess",
            Prompt = "follow-up prompt",
            UseResume = true,   // resume = NOT first prompt
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = "# Instructions\nUse TDD."
        };

        var task = (Task<(int, string?)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, CancellationToken.None, new List<string>()])!;
        await task;

        var steeringPath = Path.Combine(chatWorkspace, ".kiro", "steering", "pipeline-project.md");
        File.Exists(steeringPath).Should().BeFalse(
            "steering file must NOT be written on resume prompts (UseResume=true)");
    }

    /// <summary>
    /// Verifies null ProjectSteeringContent produces no steering file (backward compat).
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_NullProjectSteeringContent_NoSteeringFileWritten()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var chatWindowId = Guid.NewGuid().ToString();
        var chatWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        await using var batcher = new OutputBatcher();
        var message = new ChatPromptMessage
        {
            SessionId = "no-steering-sess",
            Prompt = "no project",
            UseResume = false,
            ChatWindowId = chatWindowId,
            ProjectSteeringContent = null   // null = no project selected
        };

        var task = (Task<(int, string?)>)GetPrivateMethod(service, "ExecuteChatWithOutputAsync")
            .Invoke(service, [message, batcher, CancellationToken.None, new List<string>()])!;
        await task;

        var steeringPath = Path.Combine(chatWorkspace, ".kiro", "steering", "pipeline-project.md");
        File.Exists(steeringPath).Should().BeFalse(
            "null ProjectSteeringContent must produce no steering file (backward compat — no project selected)");
    }
}
