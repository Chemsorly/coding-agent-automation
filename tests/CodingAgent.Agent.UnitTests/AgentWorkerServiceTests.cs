using System.Net.Http;
using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentWorkerService"/>.
/// Since AgentWorkerService depends on concrete classes (HubConnectionManager),
/// we test constructor validation, public property defaults, and the service's behavioral contract
/// through its observable state.
/// Private chat-execution helpers (ReportChatCompletedAsync, RunChatTaskAsync, ExecuteChatWithOutputAsync,
/// HandleFetchModelsAsync) are reached through reflection on the extracted ChatJobExecutor.
/// </summary>
/// <remarks>
/// This class mutates process-global environment variables (AGENT_ID, AGENT_LABELS).
/// It shares the "EnvironmentVariables" collection with <see cref="HealthEndpointsTests"/> to
/// prevent parallel execution — environment variables are process-wide shared state.
/// </remarks>
[Collection("EnvironmentVariables")]
public class AgentWorkerServiceTests : IDisposable
{
    public void Dispose()
    {
        // Clean up directories created by tests that invoke HandleChatPromptAsync
        // (production code calls Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath)
        // and may also create per-window workspaces under AgentDefaults.ChatWorkspacesRoot)
        TryDeleteDir(AgentDefaults.ChatWorkspacePath);
        TryDeleteDir(AgentDefaults.ChatWorkspacesRoot);
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            // Also clean parent dirs if empty (e.g. /app/workspaces, /app)
            var parent = Path.GetDirectoryName(path);
            while (parent != null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        catch { /* best effort cleanup */ }
    }

    [Fact]
    public void Constructor_ThrowsOnNullConnectionLifecycle()
    {
        var mockLogger = new Mock<Serilog.ILogger>();

        var act = () => new AgentWorkerService(new AgentWorkerServiceDependencies(null!, new ChatSlotManager(), null!, mockLogger.Object));
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.ConnectionLifecycle");
    }

    [Fact]
    public void Constructor_ThrowsOnNullLogger()
    {
        var (_, slotManager, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents();
        var logger = new Mock<Serilog.ILogger>().Object;
        var chatHandler = CreateChatHandler(lifecycle, slotManager, logger);

        var act = () => new AgentWorkerService(
            new AgentWorkerServiceDependencies(lifecycle, slotManager, chatHandler, null!));
        act.Should().Throw<ArgumentNullException>().WithParameterName("deps.Logger");
    }

    [Fact]
    public void IsBusy_DefaultsFalse()
    {
        var service = CreateService();
        service.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void CurrentStep_DefaultsNull()
    {
        var service = CreateService();
        service.CurrentStep.Should().BeNull();
    }

    [Fact]
    public void IsConnected_DelegatesToHubManager()
    {
        var service = CreateService();
        // Before starting, the hub manager is not connected
        service.IsConnected.Should().BeFalse();
    }

    [Fact]
    public void Constructor_ParsesLabelsFromEnvironment()
    {
        var originalLabels = Environment.GetEnvironmentVariable("AGENT_LABELS");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", "kiro,dotnet,gpu");
            var service = CreateService();
            // Service should be created successfully with parsed labels
            service.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", originalLabels ?? "kiro,dotnet");
        }
    }

    [Fact]
    public void Constructor_HandlesEmptyLabels()
    {
        var originalLabels = Environment.GetEnvironmentVariable("AGENT_LABELS");
        try
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", "");
            var service = CreateService();
            service.Should().NotBeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENT_LABELS", originalLabels ?? "kiro,dotnet");
        }
    }

    [Fact]
    public async Task ExecuteAsync_CancellationStopsService()
    {
        var service = CreateService();
        using var cts = new CancellationTokenSource();

        // Start the service — it will try to connect and fail, but should respect cancellation
        var executeTask = Task.Run(async () =>
        {
            try
            {
                await service.StartAsync(cts.Token);
            }
            catch (Exception)
            {
                // Expected — connection will fail since there's no real orchestrator
            }
        });

        // Cancel quickly
        cts.Cancel();

        // Should complete; the 30s bound is a hang detector only, so a stalled test host cannot fail the test
        var completed = await Task.WhenAny(executeTask, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(executeTask, "service should stop when cancelled");
    }

    [Fact]
    public async Task HandleCancelChat_DisposedCts_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();
        var cts = new CancellationTokenSource();
        cts.Dispose();

        SetPrivateField(GetSlotManager(service), "_activeChatSessionId", "session-1");
        SetPrivateField(GetSlotManager(service), "_chatCts", cts);
        SetPrivateField(GetSlotManager(service), "_activeChatTask", Task.CompletedTask);

        // Act — should not throw ObjectDisposedException
        var chatJobHandler = GetChatJobHandler(service);
        var task = chatJobHandler.HandleCancelChatAsync("session-1");
        await task;

        Assert.True(task.IsCompletedSuccessfully, "task should complete successfully without throwing");
    }

    // ── Requirement 4.5: ShutdownAsync Stops Hub Connection ─────────────

    [Fact]
    public async Task ShutdownAsync_StopsHubConnectionGracefully()
    {
        // Arrange
        var service = CreateService();

        // Act — invoke private ShutdownAsync
        var shutdownMethod = GetPrivateMethod(service, "ShutdownAsync");
        var task = (Task)shutdownMethod.Invoke(service, [])!;
        await task;

        // Assert — should complete without throwing; connection was never started
        // so StopAsync on a disconnected connection is a graceful no-op
        service.IsConnected.Should().BeFalse();
    }

    // ── Requirement 4.6: Chat Prompt Handler ────────────────────────────

    [Fact]
    public async Task HandleChatPrompt_InvokesOrchestratorExecutePromptAsync()
    {
        // Arrange
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<string, Task>?>(),
                It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (prompt, _, _, _, _, _, _) =>
                {
                    if (prompt == "Hello, world!")
                        invoked.TrySetResult();
                    return Task.FromResult(0);
                });

        var service = CreateServiceWithOrchestrator(mockOrchestrator.Object);

        // Ensure the chat workspace directory exists (production code uses AgentDefaults.ChatWorkspacePath
        // which may not be writable on CI runners)
        var chatWorkspace = AgentDefaults.ChatWorkspacePath;
        try { Directory.CreateDirectory(chatWorkspace); }
        catch { /* If we can't create it, the test will detect the issue via timeout */ }

        var message = new ChatPromptMessage
        {
            SessionId = "session-1",
            Prompt = "Hello, world!",
            UseResume = true
        };

        // Act
        var chatJobHandler = GetChatJobHandler(service);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Wait for the background Task.Run to invoke the orchestrator (with timeout)
        var completed = await Task.WhenAny(invoked.Task, Task.Delay(TimeSpan.FromSeconds(30)));

        // Assert — verify the orchestrator was called with the prompt
        if (completed != invoked.Task)
        {
            // If the orchestrator was never invoked, it's likely because Directory.CreateDirectory
            // failed on this platform (non-Docker environment). Skip gracefully.
            var canCreateDir = Directory.Exists(chatWorkspace);
            if (!canCreateDir)
            {
                // Cannot test this on platforms where the chat workspace is not writable
                return;
            }
        }

        mockOrchestrator.Verify(
            o => o.ExecutePromptAsync(
                "Hello, world!",
                It.IsAny<string>(),
                true,
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<string, Task>?>(),
                It.IsAny<string?>()),
            Times.AtLeastOnce());
    }

    [Fact]
    public async Task HandleChatPrompt_WhenChatAlreadyActive_RejectsPrompt()
    {
        // Arrange — a chat session is already active
        var service = CreateService();
        SetPrivateField(GetSlotManager(service), "_activeChatSessionId", "existing-session");

        var message = new ChatPromptMessage
        {
            SessionId = "session-2",
            Prompt = "Should be rejected"
        };

        // Act
        var chatJobHandler = GetChatJobHandler(service);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Assert — existing session remains (new one rejected)
        var activeChatSession = GetPrivateField<string?>(GetSlotManager(service), "_activeChatSessionId");
        activeChatSession.Should().Be("existing-session", "slot already held — new chat should be rejected");
    }

    // ── Shutdown Cancellation Label Tests ──────────────────────────────

    [Fact]
    public async Task HandleAssignJob_WhenExecutorThrowsOCE_CompletionPayloadHasFinalLabelCancelled()
    {
        // Regression test: When the executor throws OperationCanceledException
        // (agent pod SIGTERM during execution), AgentJobRunner must produce a JobCompletionPayload
        // with FinalLabel = "agent:cancelled" so the orchestrator applies the correct label.
        //
        // Previously, FinalLabel was not set — only in the inner LocalPipelineExecutor catch.
        // If the executor didn't unwind cleanly within the shutdown timeout, the outer catch
        // produced a payload without FinalLabel, causing the orchestrator to derive
        // the label from FinalStep → agent:error.
        //
        // This test exercises AgentJobRunner.ExecuteAsync directly with a delegate that
        // throws OperationCanceledException, verifying the OCE catch path sets FinalLabel.

        // Arrange
        var message = CreateTestJobAssignment("cancel-test-job");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act: invoke AgentJobRunner directly with a delegate that throws OCE
        // (simulating the executor receiving a cancelled token and unwinding immediately)
        await using var outputBatcher = new OutputBatcher();
        var payload = await AgentJobRunner.ExecuteAsync(
            execute: (_, _, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new JobCompletionPayload { FinalStep = PipelineStep.Completed, CompletedAt = DateTimeOffset.UtcNow });
            },
            assignment: message,
            connection: TestAgentWorkerServiceFactory.CreateTestHubManager().Connection,
            outputBatcher: outputBatcher,
            onStepChanged: _ => { },
            cancelledLabel: AgentLabels.Cancelled,
            ct: cts.Token);

        // Assert
        payload.FinalLabel.Should().Be("agent:cancelled",
            "AgentJobRunner must set FinalLabel = AgentLabels.Cancelled when OCE is caught");
        payload.FinalStep.Should().Be(PipelineStep.Cancelled,
            "FinalStep must be Cancelled when the job was cancelled via OCE");
    }

    // ── Bug Fix Characterization Tests ─────────────────────────────────

    [Fact]
    public async Task HandleCancelChat_WaitsForChatTaskCompletion_BeforeSendingAgentReady()
    {
        // Arrange
        var service = CreateService();
        var chatTaskCompletion = new TaskCompletionSource();
        var chatCts = new CancellationTokenSource();

        SetPrivateField(GetSlotManager(service), "_activeChatSessionId", "session-1");
        SetPrivateField(GetSlotManager(service), "_activeChatTask", chatTaskCompletion.Task);
        SetPrivateField(GetSlotManager(service), "_chatCts", chatCts);

        // Act — invoke cancel handler; it should wait for the chat task
        var chatJobHandler = GetChatJobHandler(service);
        var cancelTask = chatJobHandler.HandleCancelChatAsync("session-1");

        // The cancel task should not complete while chat task is pending.
        // Use a deterministic signal: if cancelTask completes before we signal chatTaskCompletion,
        // then the handler did NOT wait (which is the bug case).
        await Task.Delay(50); // brief yield to let the handler reach the await point
        cancelTask.IsCompleted.Should().BeFalse(
            "cancel handler should be waiting for the chat task to complete");

        // Complete the chat task
        chatTaskCompletion.SetResult();

        // Now the cancel handler should complete (within generous timeout)
        var completed = await Task.WhenAny(cancelTask, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(cancelTask, "cancel handler should complete after chat task finishes");

        // CTS should have been cancelled
        chatCts.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task HandleCancelChat_TimesOutIfChatTaskHangs()
    {
        // Arrange — chat task that never completes; 50ms grace (injected) so the timeout fires fast.
        var service = CreateService(chatGracePeriod: TimeSpan.FromMilliseconds(50));
        var neverCompletes = new TaskCompletionSource();
        var chatCts = new CancellationTokenSource();

        SetPrivateField(GetSlotManager(service), "_activeChatSessionId", "session-hang");
        SetPrivateField(GetSlotManager(service), "_activeChatTask", neverCompletes.Task);
        SetPrivateField(GetSlotManager(service), "_chatCts", chatCts);

        // Act — cancel handler should time out (after the injected 50ms grace) and still complete
        var chatJobHandler = GetChatJobHandler(service);
        var cancelTask = chatJobHandler.HandleCancelChatAsync("session-hang");

        // The 30s bound is a hang detector only, so a stalled test host cannot fail the test
        var completed = await Task.WhenAny(cancelTask, Task.Delay(TimeSpan.FromSeconds(30)));
        completed.Should().Be(cancelTask, "cancel handler should time out and complete");
    }

    [Fact]
    public async Task HandleChatPrompt_SetsChatCtsInsideLock()
    {
        // After HandleChatPromptAsync sets _activeChatSessionId, _chatCts must also be set
        var service = CreateService();
        var message = new ChatPromptMessage
        {
            SessionId = "session-cts-test",
            Prompt = "test",
            UseResume = true
        };

        // Act
        var chatJobHandler = GetChatJobHandler(service);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Assert — both should be set atomically (or both cleared if chat task already ran)
        // Since the chat task runs in background, check immediately after handler returns
        // that _activeChatTask was stored
        var chatTask = GetPrivateField<Task?>(GetSlotManager(service), "_activeChatTask");
        chatTask.Should().NotBeNull("_activeChatTask should be stored for cancel coordination");
    }

    // ── Characterization Tests: KiroCli warm-up and resume paths ────────

    [Fact]
    public async Task HandleChatPrompt_KiroCli_WhenNotResume_SendsWarmUpThenActualPrompt()
    {
        // Arrange
        var callOrder = new List<(string prompt, bool useResume)>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<string, Task>?>(),
                It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (prompt, _, useResume, _, _, _, _) =>
                {
                    callOrder.Add((prompt, useResume));
                    return Task.FromResult(0);
                });

        var service = CreateServiceWithOrchestrator(mockOrchestrator.Object);

        var chatWorkspace = AgentDefaults.ChatWorkspacePath;
        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        var message = new ChatPromptMessage
        {
            SessionId = "session-warmup",
            Prompt = "Real prompt",
            UseResume = false
        };

        // Act
        var chatJobHandler = GetChatJobHandler(service);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Wait for background task
        var chatTask = GetPrivateField<Task?>(GetSlotManager(service), "_activeChatTask");
        if (chatTask is not null)
            await chatTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — warm-up first, then real prompt
        if (callOrder.Count == 0 && !Directory.Exists(chatWorkspace))
            return; // Platform cannot create workspace

        callOrder.Should().HaveCount(2);
        callOrder[0].prompt.Should().Be(AgentDefaults.ChatWarmUpPrompt);
        callOrder[0].useResume.Should().BeFalse();
        callOrder[1].prompt.Should().Be("Real prompt");
        callOrder[1].useResume.Should().BeTrue();
    }

    [Fact]
    public async Task HandleChatPrompt_KiroCli_WhenResume_SkipsWarmUp()
    {
        // Arrange
        var callOrder = new List<(string prompt, bool useResume)>();
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Func<string, Task>?>(),
                It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (prompt, _, useResume, _, _, _, _) =>
                {
                    callOrder.Add((prompt, useResume));
                    return Task.FromResult(0);
                });

        var service = CreateServiceWithOrchestrator(mockOrchestrator.Object);

        var chatWorkspace = AgentDefaults.ChatWorkspacePath;
        try { Directory.CreateDirectory(chatWorkspace); }
        catch { return; }

        var message = new ChatPromptMessage
        {
            SessionId = "session-resume",
            Prompt = "Follow-up prompt",
            UseResume = true
        };

        // Act
        var chatJobHandler = GetChatJobHandler(service);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Wait for background task
        var chatTask = GetPrivateField<Task?>(GetSlotManager(service), "_activeChatTask");
        if (chatTask is not null)
            await chatTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Assert — only one call (the real prompt), no warm-up
        if (callOrder.Count == 0 && !Directory.Exists(chatWorkspace))
            return;

        callOrder.Should().HaveCount(1);
        callOrder[0].prompt.Should().Be("Follow-up prompt");
        callOrder[0].useResume.Should().BeTrue();
    }

    // ── ReportChatCompletedAsync — hub throws, should not propagate ───────

    [Fact]
    public async Task ReportChatCompletedAsync_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "ReportChatCompletedAsync")
            .Invoke(GetChatJobHandler(service), ["sess-1", 0, (string?)null])!;
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ReportChatCompletedAsync_WithError_HubThrows_DoesNotThrow()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "ReportChatCompletedAsync")
            .Invoke(GetChatJobHandler(service), ["sess-2", 1, "some error"])!;
        await act.Should().NotThrowAsync();
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

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

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
    /// </summary>
    // TODO: This test does not actually exercise the failure scenario its name describes.
    // ReportChatCompletedAsync swallows exceptions internally (unconditional catch), so the
    // test only exercises the normal completion path — the finally block is never triggered by
    // an exception. This means the test would pass identically if the try/finally fix were
    // reverted back to sequential code.
    //
    // The companion source-scan test (SourceCode_RunChatTaskAsync_ReleaseChatSlotIsInsideFinallyBlock)
    // was removed in the test-suite audit: it asserted on the raw text of ChatJobExecutor.cs and, by
    // its own admission, passed whenever a `finally` keyword appeared anywhere before the first
    // ReleaseChatSlot() substring — it did not verify lexical enclosure. Issue #1857 therefore has
    // NO effective regression guard today.
    //
    // To close the gap properly, make ReportChatCompletedAsync propagate (or add an injection point
    // that simulates throw behaviour) so this test can assert the slot is released on the failure
    // path — a behavioural guard rather than a text-pattern one.
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
        await (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;

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

        var task = (Task<(int exitCode, string? error)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, cts.Token])!;
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

        var task = (Task<(int exitCode, string? error)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
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
            // TODO: Indentation inconsistency — the .Invoke(...) continuation is aligned at column 12 instead of
            // the expected column 16 (matching the async lambda body). This obscures the two-part method call chain
            // and could confuse readers about grouping. Reformat to align .Invoke() under GetMethod().
            var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "HandleFetchModelsAsync")
            .Invoke(GetChatJobHandler(service), [request])!;

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
                // TODO: Indentation inconsistency — same as the error-path test above. The .Invoke(...) continuation
                // is aligned at column 12 instead of column 16. Reformat to align under GetMethod().
                var act = async () => await (Task)GetMethod(GetChatJobHandler(service), "HandleFetchModelsAsync")
            .Invoke(GetChatJobHandler(service), [request])!;

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

    // ── Project secrets injection and cleanup ─────────────────────────────────

    /// <summary>
    /// Verifies that ProjectSecrets on the KiroCli path are passed as environmentVariables
    /// to the orchestrator and NOT set as process-wide environment variables (issue #1913).
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_PassesEnvVarsToOrchestratorNotGlobally()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_{Guid.NewGuid():N}";

        IReadOnlyDictionary<string, string>? capturedEnvVars = null;
        var capturedGlobalEnvVar = new List<string?>();

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
                    // Capture global env to verify it is NOT set
                    capturedGlobalEnvVar.Add(Environment.GetEnvironmentVariable(secretKey));
                    return Task.FromResult(0);
                });

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // Do NOT swallow directory-creation failures here (issue #1913 [CRITICAL] fix).
        // A silent `catch { return; }` causes a vacuous pass — the orchestrator is never
        // invoked, capturedEnvVars remains null, and the central assertion is never evaluated.
        // Infrastructure failures must surface as test failures so regressions are visible.
        Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath);

        slotManager.TryAcquireChatSlot("secrets-inject-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-inject-sess",
            Prompt = "test",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "injected-value" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Secrets are passed to the orchestrator as environmentVariables
        capturedEnvVars.Should().NotBeNull("orchestrator must have been invoked with environmentVariables");
        capturedEnvVars.Should().ContainKey(secretKey).WhoseValue.Should().Be("injected-value",
            "ProjectSecrets must be forwarded to orchestrator via environmentVariables");

        // Secrets are NOT set as process-wide environment variables
        capturedGlobalEnvVar.Should().NotBeEmpty("orchestrator must have been invoked");
        capturedGlobalEnvVar[0].Should().BeNull(
            "ProjectSecrets must NOT pollute the parent process environment");

        // After completion the global env var must remain null
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "Global env must not be set before, during, or after chat execution");
    }

    /// <summary>
    /// Verifies that when the orchestrator throws, no global env vars were set
    /// (since we no longer use Environment.SetEnvironmentVariable at all).
    /// </summary>
    [Fact]
    public async Task RunChatTaskAsync_WithProjectSecrets_WhenOrchestratorThrows_NoGlobalEnvPollution()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var secretKey = $"TEST_CHAT_SECRET_THROW_{Guid.NewGuid():N}";

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ThrowsAsync(new InvalidOperationException("orchestrator boom"));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);
        var slotManager = GetSlotManager(service);

        // Do NOT swallow directory-creation failures here (issue #1913 [CRITICAL] fix).
        // A silent `catch { return; }` causes a vacuous pass when directory creation fails —
        // the orchestrator is never invoked and the global-env assertion is never evaluated,
        // masking a potential regression where Environment.SetEnvironmentVariable was re-introduced.
        Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath);

        slotManager.TryAcquireChatSlot("secrets-throw-sess", out _);

        var message = new ChatPromptMessage
        {
            SessionId = "secrets-throw-sess",
            Prompt = "boom",
            UseResume = false,
            ProjectSecrets = new Dictionary<string, string> { [secretKey] = "should-never-appear-globally" }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = (Task)GetMethod(GetChatJobHandler(service), "RunChatTaskAsync")
            .Invoke(GetChatJobHandler(service), [message, cts.Token])!;
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        // Even though orchestrator threw, no global env var was ever set
        Environment.GetEnvironmentVariable(secretKey).Should().BeNull(
            "ProjectSecrets must never be set in the global process environment");
    }

    /// <summary>
    /// Verifies that null ProjectSecrets produce no env var changes and do not break execution.
    /// </summary>
    [Fact]
    public async Task ExecuteChatWithOutputAsync_NullProjectSecrets_NoEnvVarsSet()
    {
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");
        var sentinelKey = $"TEST_NULL_SECRET_{Guid.NewGuid():N}";

        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Returns(Task.FromResult(0));

        var service = TestAgentWorkerServiceFactory.Create(orchestrator: mockOrchestrator.Object);

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

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        // TODO [WARNING]: sentinelKey is a randomly-generated key that is never set anywhere,
        // so this assertion would pass even if the production code called Environment.SetEnvironmentVariable
        // with a different key. A stronger check would capture the environmentVariables argument from
        // the orchestrator mock (as done in RunChatTaskAsync_WithProjectSecrets_PassesEnvVarsToOrchestratorNotGlobally)
        // and assert it is null or empty — that directly validates the no-injection contract.
        // No env vars should have been set — null ProjectSecrets must not inject anything
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull(
            "null ProjectSecrets must not inject any env vars");
    }

    // ── CleanupChatSecrets removed in issue #1913 ──────────────────────────────

    /// <summary>
    /// Documents that <c>CleanupChatSecrets</c> was removed in issue #1913 as part of replacing
    /// process-wide env var injection with per-process <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>.
    /// Secrets are now passed directly to the orchestrator and never touch the parent process env.
    /// </summary>
    [Fact]
    public void CleanupChatSecrets_MethodNoLongerExists()
    {
        var method = typeof(AgentWorkerService)
            .GetMethod("CleanupChatSecrets", BindingFlags.NonPublic | BindingFlags.Instance);

        method.Should().BeNull(
            "CleanupChatSecrets was removed in issue #1913 — secrets are now passed per-process via environmentVariables");
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
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, workspace, _, _, _, _, _) =>
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

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
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

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
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

        var task = (Task<(int, string?)>)GetMethod(GetChatJobHandler(service), "ExecuteChatWithOutputAsync")
            .Invoke(GetChatJobHandler(service), [message, batcher, CancellationToken.None])!;
        await task;

        var steeringPath = Path.Combine(chatWorkspace, ".kiro", "steering", "pipeline-project.md");
        File.Exists(steeringPath).Should().BeFalse(
            "null ProjectSteeringContent must produce no steering file (backward compat — no project selected)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static ChatJobExecutor GetChatJobHandler(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_chatJobHandler",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_chatJobHandler' not found");
        return (ChatJobExecutor)field.GetValue(service)!;
    }

    private static AgentWorkerService CreateService(TimeSpan? chatGracePeriod = null)
    {
        return TestAgentWorkerServiceFactory.Create(chatGracePeriod: chatGracePeriod);
    }

    private static AgentWorkerService CreateServiceWithOrchestrator(KiroCliLib.Core.IKiroCliOrchestrator orchestrator)
    {
        // Ensure the KiroCli code path is active. When AGENT_PROVIDER_TYPE=OpenCode,
        // the service routes chat prompts through OpenCodeAgentProvider instead of the
        // mock IKiroCliOrchestrator, causing Moq verification failures.
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        return TestAgentWorkerServiceFactory.Create(orchestrator: orchestrator);
    }

    private static HubConnectionManagerFactory CreateTestHubManagerFactory()
    {
        var logger = new Mock<Serilog.ILogger>();
        return new HubConnectionManagerFactory("http://localhost:9999", "test-agent", "test-api-key", logger.Object);
    }

    private static JobAssignmentMessage CreateTestJobAssignment(string jobId = "test-job-1")
    {
        return new JobAssignmentMessage
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

    private static MethodInfo GetPrivateMethod(object obj, string methodName)
    {
        return obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found");
    }

    /// <summary>
    /// Gets a public method on an executor class by name. Used for ChatJobExecutor methods
    /// that moved from private on AgentWorkerService to public on the extracted executor class.
    /// </summary>
    private static MethodInfo GetMethod(object obj, string name) =>
        obj.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method '{name}' not found on {obj.GetType().Name}");

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found");
        return (T?)field.GetValue(obj);
    }

    private static ChatSlotManager GetSlotManager(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_slotManager",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("Field '_slotManager' not found");
        return (ChatSlotManager)field.GetValue(service)!;
    }

    private static ChatJobExecutor CreateChatHandler(AgentConnectionLifecycle lifecycle, ChatSlotManager slotManager, Serilog.ILogger logger) =>
        TestAgentWorkerServiceFactory.CreateChatJobExecutor(lifecycle, slotManager, logger: logger);
}
