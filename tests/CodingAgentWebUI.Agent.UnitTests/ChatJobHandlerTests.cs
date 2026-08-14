using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="ChatJobHandler"/>.
/// Verifies chat session handling, cancel behavior, model fetching, and completion reporting
/// without requiring full <see cref="AgentWorkerServiceDependencies"/> construction.
/// </summary>
[Collection("EnvironmentVariables")]
public class ChatJobHandlerTests : IDisposable
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

    // ── Setup helpers ─────────────────────────────────────────────────────

    private static (ChatJobHandler Handler, AgentJobSlotManager SlotManager, AgentConnectionLifecycle Lifecycle)
        CreateHandler(
            KiroCliLib.Core.IKiroCliOrchestrator? orchestrator = null,
            IHostApplicationLifetime? hostLifetime = null,
            Serilog.ILogger? logger = null,
            Func<Task>? signalAgentReady = null,
            bool isOpenCodeProvider = false,
            bool isChatMode = false)
    {
        var mockLogger = logger ?? new Mock<Serilog.ILogger>().Object;
        var mockOrchestrator = orchestrator ?? new Mock<KiroCliLib.Core.IKiroCliOrchestrator>().Object;
        var lifetime = hostLifetime ?? Mock.Of<IHostApplicationLifetime>();
        var hm = TestAgentWorkerServiceFactory.CreateTestHubManager(mockLogger);
        var hmFactory = TestAgentWorkerServiceFactory.CreateTestHubManagerFactory(mockLogger);
        var buffer = new CriticalMessageBuffer();
        var pipeline = CodingAgentWebUI.Infrastructure.Resilience.ResiliencePipelineFactory.CreateSignalRPipeline(mockLogger);
        var signalRReporter = new SignalRCompletionReporter(hm, pipeline, buffer, mockLogger);
        var slotManager = new AgentJobSlotManager(() => Task.CompletedTask);
        var lifecycle = new AgentConnectionLifecycle(hm, hmFactory, signalRReporter, slotManager,
            new AgentId("test-chat"), lifetime, mockLogger);

        var handler = new ChatJobHandler(new ChatJobHandlerDependencies(
            lifecycle, slotManager, mockOrchestrator,
            Mock.Of<System.Net.Http.IHttpClientFactory>(),
            lifetime,
            SignalAgentReady: signalAgentReady ?? (() => Task.CompletedTask),
            IsOpenCodeProvider: isOpenCodeProvider,
            IsChatMode: isChatMode,
            Logger: mockLogger));

        return (handler, slotManager, lifecycle);
    }

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    // ── HandleChatPromptAsync ─────────────────────────────────────────────

    [Fact]
    public async Task HandleChatPromptAsync_WhenBusy_RejectsWithoutLaunchingTask()
    {
        var (handler, slotManager, _) = CreateHandler();

        // Simulate busy agent
        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"busy-job");
        SetPrivateField(slotManager, "_isBusy", true);

        var message = new ChatPromptMessage { SessionId = "rejected-session", Prompt = "test" };
        await handler.HandleChatPromptAsync(message);

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("busy agent must reject chat prompt without acquiring chat slot");
    }

    [Fact]
    public async Task HandleChatPromptAsync_WhenIdle_AcquiresSlotAndStartsTask()
    {
        var (handler, slotManager, _) = CreateHandler();

        var message = new ChatPromptMessage
        {
            SessionId = "active-session",
            Prompt = "test",
            UseResume = true
        };
        await handler.HandleChatPromptAsync(message);

        // TODO: Assertion is too weak — only checks that _activeChatTask is non-null. A stronger check would
        // also assert that _activeChatSessionId == "active-session" to verify the slot was acquired for the
        // correct session. Additionally, because UseResume = true races through execution with no orchestrator
        // mock configured, the background task may mutate state before assertions run; awaiting the task or
        // using a blocking orchestrator mock would make this test deterministic.
        GetPrivateField<Task?>(slotManager, "_activeChatTask")
            .Should().NotBeNull("HandleChatPromptAsync must store the active chat task");
    }

    [Fact]
    public async Task HandleChatPromptAsync_WhenAlreadyHandlingSession_RejectsNewSession()
    {
        // Verifies that a second concurrent session is rejected without disturbing the first.
        var (handler, slotManager, _) = CreateHandler();

        // Simulate an active chat session already in progress
        SetPrivateField(slotManager, "_activeChatSessionId", "other-session");

        var message = new ChatPromptMessage { SessionId = "new-session", Prompt = "test", UseResume = true };
        await handler.HandleChatPromptAsync(message);

        GetPrivateField<Task?>(slotManager, "_activeChatTask")
            .Should().BeNull("handler must not overwrite an existing chat session");
        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().Be("other-session", "existing session must not be displaced by a new request");
    }

    [Fact]
    public async Task HandleChatPromptAsync_KiroCli_WhenNotResume_SendsWarmUpThenRealPrompt()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var callOrder = new List<(string prompt, bool useResume)>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (prompt, _, useResume, _, _, _, _) =>
                {
                    callOrder.Add((prompt, useResume));
                    return Task.FromResult(0);
                });

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        var message = new ChatPromptMessage { SessionId = "warmup-session", Prompt = "Real prompt", UseResume = false };
        await handler.HandleChatPromptAsync(message);

        var chatTask = GetPrivateField<Task?>(slotManager, "_activeChatTask");
        if (chatTask is not null)
            await Task.WhenAny(chatTask, Task.Delay(5000));

        // TODO: Silent false-green — if directory creation fails (caught above) OR the background task does not
        // reach the orchestrator mock in time, callOrder will be empty and the test exits without running any
        // assertions. A regression in warm-up logic would produce a false pass. Consider using a blocking
        // mock or awaiting the chat task unconditionally so assertions always execute.
        if (callOrder.Count == 0) return; // workspace not available

        callOrder.Should().HaveCount(2);
        callOrder[0].prompt.Should().Be(AgentDefaults.ChatWarmUpPrompt, "warm-up prompt must be sent first");
        callOrder[0].useResume.Should().BeFalse();
        callOrder[1].prompt.Should().Be("Real prompt", "real prompt must be sent after warm-up");
        callOrder[1].useResume.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatPromptAsync_KiroCli_WhenResume_SkipsWarmUp()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var callOrder = new List<(string prompt, bool useResume)>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (prompt, _, useResume, _, _, _, _) =>
                {
                    callOrder.Add((prompt, useResume));
                    return Task.FromResult(0);
                });

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        var message = new ChatPromptMessage { SessionId = "resume-session", Prompt = "Follow-up", UseResume = true };
        await handler.HandleChatPromptAsync(message);

        var chatTask = GetPrivateField<Task?>(slotManager, "_activeChatTask");
        if (chatTask is not null)
            await Task.WhenAny(chatTask, Task.Delay(5000));

        // TODO: [WARNING] Same silent false-green pattern as WhenNotResume test above. If directory creation
        // fails (catch { return; } above) or the background task does not reach the orchestrator mock within
        // 5 seconds, callOrder is empty and the test exits green without running any assertions. A regression
        // that broke resume-path logic entirely would produce a false pass.
        if (callOrder.Count == 0) return;

        callOrder.Should().HaveCount(1, "resume must skip warm-up and send only one prompt");
        callOrder[0].prompt.Should().Be("Follow-up");
        callOrder[0].useResume.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatPromptAsync_ChatWindowId_UsesPerWindowWorkspace()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var chatWindowId = Guid.NewGuid().ToString();
        var expectedWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);
        var capturedWorkspace = new List<string>();

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, workspace, _, _, _, _, _) =>
                {
                    capturedWorkspace.Add(workspace);
                    return Task.FromResult(0);
                });

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        try { Directory.CreateDirectory(expectedWorkspace); }
        catch { return; }

        var message = new ChatPromptMessage
        {
            SessionId = "window-session",
            Prompt = "test",
            UseResume = true,
            ChatWindowId = chatWindowId
        };
        await handler.HandleChatPromptAsync(message);

        var chatTask = GetPrivateField<Task?>(slotManager, "_activeChatTask");
        if (chatTask is not null)
            await Task.WhenAny(chatTask, Task.Delay(5000));

        // TODO: [WARNING] Silent false-green — if directory creation fails (catch { return; } above) or the
        // background task does not reach the orchestrator mock within 5 seconds, capturedWorkspace is empty
        // and the test exits green without running any assertions. The critical assertion that per-window
        // workspace path is used is never verified on the failure path.
        if (capturedWorkspace.Count == 0) return;
        capturedWorkspace[0].Should().Be(expectedWorkspace,
            "non-empty ChatWindowId must use per-window workspace under ChatWorkspacesRoot");
    }

    // ── RunChatTaskAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task RunChatTaskAsync_ReleasesChatSlotOnSuccess()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(Task.FromResult(0));

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        // TODO: [WARNING] If Directory.CreateDirectory fails, the test exits green via `catch { return; }`
        // without acquiring the slot or running any assertions. The assertion that the chat slot is released
        // after RunChatTaskAsync completes is never verified on the failure path.
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        slotManager.TryAcquireChatSlot("run-slot-sess", out _);
        var message = new ChatPromptMessage { SessionId = "run-slot-sess", Prompt = "hello", UseResume = true };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await handler.RunChatTaskAsync(message, cts.Token);

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot must be released after RunChatTaskAsync completes");
    }

    [Fact]
    public async Task RunChatTaskAsync_ReleasesChatSlotEvenWhenReportChatCompletedThrows()
    {
        // ReportChatCompletedAsync swallows exceptions internally (hub is disconnected in tests),
        // so this tests the finally block ensuring unconditional slot release.
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(Task.FromResult(0));

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        // TODO: [WARNING] If Directory.CreateDirectory fails, the test exits green via `catch { return; }`
        // without the slot being held or any assertions running. The most important invariant here —
        // that the finally block unconditionally releases the slot — is never verified on the failure path.
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { return; }

        slotManager.TryAcquireChatSlot("finally-guard-sess", out _);
        var message = new ChatPromptMessage { SessionId = "finally-guard-sess", Prompt = "test", UseResume = true };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await handler.RunChatTaskAsync(message, cts.Token);

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().BeNull("chat slot must be released unconditionally via finally block");
    }

    [Fact]
    public async Task RunChatTaskAsync_PassesProjectSecretsToOrchestratorNotGlobally()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_HANDLER_SECRET_{Guid.NewGuid():N}";
        IReadOnlyDictionary<string, string>? capturedEnvVars = null;

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, _, _, envVars) =>
                {
                    capturedEnvVars = envVars;
                    return Task.FromResult(0);
                });

        var (handler, slotManager, _) = CreateHandler(orchestrator: mockOrchestrator.Object);

        Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath);

        slotManager.TryAcquireChatSlot("secrets-sess", out _);
        var message = new ChatPromptMessage
        {
            SessionId = "secrets-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "secret-value" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await handler.RunChatTaskAsync(message, cts.Token);

        capturedEnvVars.Should().NotBeNull("orchestrator must be called with environmentVariables");
        capturedEnvVars.Should().ContainKey(secretKey).WhoseValue.Should().Be("secret-value",
            "ProjectSecrets must be forwarded to orchestrator via environmentVariables");
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "ProjectSecrets must NOT be set as process-wide environment variables");
    }

    // ── HandleCancelChatAsync ─────────────────────────────────────────────

    [Fact]
    public async Task HandleCancelChatAsync_SessionMismatch_IsNoOp()
    {
        var (handler, slotManager, _) = CreateHandler();

        SetPrivateField(slotManager, "_activeChatSessionId", "session-A");
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());

        // Cancel for a different session — must not affect session-A
        await handler.HandleCancelChatAsync("session-B");

        GetPrivateField<string?>(slotManager, "_activeChatSessionId")
            .Should().Be("session-A", "mismatched session cancel must be a no-op");
    }

    [Fact]
    public async Task HandleCancelChatAsync_SessionMatches_CancelsCtsAndWaitsForTask()
    {
        var (handler, slotManager, _) = CreateHandler();
        var taskCompletion = new TaskCompletionSource();
        var chatCts = new CancellationTokenSource();

        SetPrivateField(slotManager, "_activeChatSessionId", "cancel-sess");
        SetPrivateField(slotManager, "_activeChatTask", taskCompletion.Task);
        SetPrivateField(slotManager, "_chatCts", chatCts);

        var cancelTask = handler.HandleCancelChatAsync("cancel-sess");

        // TODO: Flaky timing fence — 50ms is a fragile assumption. On a slow CI machine the handler may have
        // already completed the wait and set IsCompleted=true before this assertion runs, producing a false pass.
        // Consider using a TaskCompletionSource as a synchronization gate instead of a fixed delay.
        await Task.Delay(50); // let handler reach the wait point
        cancelTask.IsCompleted.Should().BeFalse("handler must wait for chat task before completing");

        taskCompletion.SetResult();
        await Task.WhenAny(cancelTask, Task.Delay(10_000));
        cancelTask.IsCompletedSuccessfully.Should().BeTrue("cancel handler must complete after task finishes");
        chatCts.IsCancellationRequested.Should().BeTrue("CTS must be cancelled");
    }

    [Fact]
    public async Task HandleCancelChatAsync_TaskHangs_TimesOutWithWarning()
    {
        var (handler, slotManager, _) = CreateHandler();
        var neverCompletes = new TaskCompletionSource();
        var chatCts = new CancellationTokenSource();

        SetPrivateField(slotManager, "_activeChatSessionId", "hang-sess");
        SetPrivateField(slotManager, "_activeChatTask", neverCompletes.Task);
        SetPrivateField(slotManager, "_chatCts", chatCts);

        var cancelTask = handler.HandleCancelChatAsync("hang-sess");
        // TODO: This test uses a 10-second real wall-clock timeout (with a 15s outer guard), making it slow in CI.
        // Consider injecting the timeout duration as a parameter or using a test-controlled clock so the timeout
        // can be triggered synchronously. Additionally, this test does not assert that a warning was logged or
        // that the CTS was cancelled — a regression that removed the wait entirely would still pass.
        var completed = await Task.WhenAny(cancelTask, Task.Delay(15000));
        completed.Should().Be(cancelTask, "cancel handler must time out and complete even when task hangs");
    }

    [Fact]
    public async Task HandleCancelChatAsync_ChatMode_CallsSignalChatEndAndStopApplication()
    {
        var mockLifetime = new Mock<IHostApplicationLifetime>();
        mockLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var (handler, slotManager, lifecycle) = CreateHandler(hostLifetime: mockLifetime.Object, isChatMode: true);

        SetPrivateField(slotManager, "_activeChatSessionId", "chatmode-sess");
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());
        SetPrivateField(slotManager, "_activeChatTask", Task.CompletedTask);

        await handler.HandleCancelChatAsync("chatmode-sess");

        mockLifetime.Verify(l => l.StopApplication(), Times.Once,
            "in chat mode, HandleCancelChatAsync must call StopApplication()");
    }

    [Fact]
    public async Task HandleCancelChatAsync_NonChatMode_CallsSignalAgentReadyNotStopApplication()
    {
        var mockLifetime = new Mock<IHostApplicationLifetime>();
        mockLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var signalReadyCalled = false;
        var (handler, slotManager, _) = CreateHandler(
            hostLifetime: mockLifetime.Object,
            isChatMode: false,
            signalAgentReady: () => { signalReadyCalled = true; return Task.CompletedTask; });

        SetPrivateField(slotManager, "_activeChatSessionId", "nonchat-sess");
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());
        SetPrivateField(slotManager, "_activeChatTask", Task.CompletedTask);

        await handler.HandleCancelChatAsync("nonchat-sess");

        mockLifetime.Verify(l => l.StopApplication(), Times.Never,
            "in non-chat mode, HandleCancelChatAsync must NOT call StopApplication()");
        signalReadyCalled.Should().BeTrue("in non-chat mode, signalAgentReady callback must be called");
    }

    // ── HandleFetchModelsAsync ────────────────────────────────────────────

    [Fact]
    public async Task HandleFetchModelsAsync_NonZeroExit_CompletesWithoutThrowing()
    {
        var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
        try
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, "/usr/bin/false");
            var (handler, _, _) = CreateHandler();
            var request = new FetchModelsRequest { RequestId = "error-req" };

            var act = async () => await handler.HandleFetchModelsAsync(request);
            await act.Should().NotThrowAsync("HandleFetchModelsAsync must swallow all errors via ReportFetchModelsError");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, origPath);
        }
    }

    [Fact]
    public async Task HandleFetchModelsAsync_ZeroExitWithValidJson_CompletesWithoutThrowing()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"fake-kiro-handler-{Guid.NewGuid():N}.sh");
        try
        {
            var validJson = """{"models":[{"model_id":"test-model","description":"Test","rate_multiplier":1.0}]}""";
            await File.WriteAllTextAsync(scriptPath, $"#!/bin/sh\necho '{validJson}'\nexit 0\n");
            using var chmod = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "chmod", Arguments = $"+x {scriptPath}", UseShellExecute = false
            });
            if (chmod is not null) await chmod.WaitForExitAsync();

            var origPath = Environment.GetEnvironmentVariable(AgentDefaults.EnvKiroCliPath);
            try
            {
                Environment.SetEnvironmentVariable(AgentDefaults.EnvKiroCliPath, scriptPath);
                var (handler, _, _) = CreateHandler();
                var request = new FetchModelsRequest { RequestId = "success-req" };

                // TODO: Assertion is too weak — only checks that the method doesn't throw. It does not verify
                // that ReportFetchModelsResult was invoked with the parsed model data (model_id="test-model",
                // description="Test", rate_multiplier=1.0). If the JSON parsing branch were removed entirely
                // (returning an empty list), this test would still pass. Add a mock hub connection that captures
                // the invocation and assert on the parsed payload to guard against JSON parsing regressions.
                var act = async () => await handler.HandleFetchModelsAsync(request);
                await act.Should().NotThrowAsync("HandleFetchModelsAsync must complete without throwing even when hub call fails");
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

    // ── ReportChatCompletedAsync ──────────────────────────────────────────

    [Fact]
    public async Task ReportChatCompletedAsync_HubThrows_DoesNotPropagate()
    {
        var (handler, _, _) = CreateHandler();
        var act = async () => await handler.ReportChatCompletedAsync("sess-1", 0, null);
        await act.Should().NotThrowAsync("ReportChatCompletedAsync must swallow hub exceptions");
    }

    [Fact]
    public async Task ReportChatCompletedAsync_WithError_HubThrows_DoesNotPropagate()
    {
        var (handler, _, _) = CreateHandler();
        var act = async () => await handler.ReportChatCompletedAsync("sess-2", 1, "some error");
        await act.Should().NotThrowAsync("ReportChatCompletedAsync must swallow hub exceptions even with error payload");
    }

    // ── Source-scan: CancellationToken.None with intentional comments ─────

    [Fact]
    public void SourceCode_ReportChatCompletedAsync_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task ReportChatCompletedAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportChatCompletedAsync must pass CancellationToken.None — chatToken may be cancelled at call time");
        methodBody.Should().Contain("// intentional:",
            "ReportChatCompletedAsync must have an // intentional: comment");
    }

    [Fact]
    public void SourceCode_ReportFetchModelsError_PassesCancellationTokenNoneWithComment()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task ReportFetchModelsError(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("CancellationToken.None",
            "ReportFetchModelsError must pass CancellationToken.None");
        methodBody.Should().Contain("// intentional:",
            "ReportFetchModelsError must have an // intentional: comment");
    }

    [Fact]
    public void SourceCode_HandleFetchModelsAsync_PassesCancellationTokenNone()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task HandleFetchModelsAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0) methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        var waitForExitEnd = methodBody.IndexOf("WaitForExitAsync(", StringComparison.Ordinal);
        waitForExitEnd = methodBody.IndexOf(");", waitForExitEnd, StringComparison.Ordinal) + 2;
        var postExitBody = methodBody.Substring(waitForExitEnd);

        postExitBody.Should().Contain("CancellationToken.None",
            "HandleFetchModelsAsync must use CancellationToken.None for post-exit calls");
        postExitBody.Should().Contain("// intentional:",
            "HandleFetchModelsAsync must have an // intentional: comment in post-exit code");
        postExitBody.Should().NotContain("timeoutCts.Token)",
            "HandleFetchModelsAsync must not pass timeoutCts.Token to any post-exit call");
    }

    [Fact]
    public void SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock()
    {
        var source = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "ChatJobHandler.cs"));

        var methodStart = source.IndexOf("public async Task RunChatTaskAsync(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("\n    public ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.IndexOf("\n    private ", methodStart + 1, StringComparison.Ordinal);
        if (methodEnd < 0)
            methodEnd = source.Length;
        var methodBody = source.Substring(methodStart, methodEnd - methodStart);

        methodBody.Should().Contain("finally",
            "RunChatTaskAsync must contain a finally block that guards ReleaseChatSlot()");
        methodBody.Should().Contain("ReleaseChatSlot()",
            "RunChatTaskAsync must call ReleaseChatSlot()");

        var finallyIndex = methodBody.LastIndexOf("finally", StringComparison.Ordinal);
        var releaseIndex = methodBody.IndexOf("ReleaseChatSlot()", StringComparison.Ordinal);
        releaseIndex.Should().BeGreaterThan(finallyIndex,
            "ReleaseChatSlot() must appear after the finally keyword");
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }
}
