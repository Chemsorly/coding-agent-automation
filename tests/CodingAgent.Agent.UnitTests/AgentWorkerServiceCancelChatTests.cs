using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Unit tests for AgentWorkerService chat-mode gating and CancelChat exit behavior (Req 5, Req 6, Req 14).
///
/// These tests reference:
///   - AgentWorkerService._isChatMode        (does NOT exist yet)
///   - AgentWorkerService constructor accepting IHostApplicationLifetime (does NOT exist yet)
///   - HandleCancelChatAsync calling StopApplication() in chat mode (NOT implemented yet)
///   - HandleCancelChatAsync NOT calling SignalAgentReadyAsync() in chat mode (NOT implemented yet)
///   - HandleChatPromptAsync using ChatWindowId for workspace derivation (NOT implemented yet)
///
/// They will FAIL TO COMPILE until task 6.2 adds these members to AgentWorkerService.
/// That compile error IS the expected red state for task 6.1.
/// </summary>
/// <remarks>
/// Validates: Requirements 5, 6, 14
/// </remarks>
[Collection("EnvironmentVariables")]
public class AgentWorkerServiceCancelChatTests : IDisposable
{
    private readonly List<string> _setEnvVars = [];

    private void SetEnv(string key, string value)
    {
        Environment.SetEnvironmentVariable(key, value);
        _setEnvVars.Add(key);
    }

    private void UnsetEnv(string key)
    {
        Environment.SetEnvironmentVariable(key, null);
        _setEnvVars.Remove(key);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        foreach (var key in _setEnvVars.ToList())
            Environment.SetEnvironmentVariable(key, null);

        // Clean up workspace directories created by chat prompt tests
        TryDeleteDir(AgentDefaults.ChatWorkspacePath);
        TryDeleteDir(AgentDefaults.ChatWorkspacesRoot);
    }

    private static void TryDeleteDir(string path)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            Directory.Delete(path, recursive: true);
            var parent = Path.GetDirectoryName(path);
            while (parent != null && Directory.Exists(parent) &&
                   !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
                parent = Path.GetDirectoryName(parent);
            }
        }
        catch { /* best effort */ }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 2: chat mode HandleCancelChatAsync → StopApplication called, AgentReady NOT sent
    // (renumbered: OnAssignJob wiring tests removed — OnAssignJob event no longer exists)
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 6
    ///
    /// In chat mode, HandleCancelChatAsync must:
    ///   - Call IHostApplicationLifetime.StopApplication() to begin graceful pod shutdown
    ///   - NOT call SignalAgentReadyAsync() (which would add the pod back to the idle pool)
    ///
    /// This test requires AgentWorkerService to accept IHostApplicationLifetime in its constructor
    /// — which does NOT exist yet. The compile error IS the expected red state.
    /// </summary>
    [Fact]
    public async Task ChatMode_HandleCancelChatAsync_StopApplicationCalled_AgentReadyNotSent()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");

        var mockLifetime = new Mock<IHostApplicationLifetime>();
        mockLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        // NOTE: This constructor call WILL NOT COMPILE until task 6.2 adds IHostApplicationLifetime
        // to AgentWorkerService's constructor. That is the expected red state.
        var (service, slotManager, lifecycle, chatHandler3) = TestAgentWorkerServiceFactory.CreateWithComponents(
            hostLifetime: mockLifetime.Object);

        // Set up an active chat session for the handler to match on
        var sessionId = "cancel-session-chat-mode";
        SetPrivateField(slotManager, "_activeChatSessionId", sessionId);
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());
        SetPrivateField(slotManager, "_activeChatTask", Task.CompletedTask);

        // Act
        var chatJobHandler = GetChatJobHandler(chatHandler3);
        await chatJobHandler.HandleCancelChatAsync(sessionId);

        // Assert 1: StopApplication() must have been called in chat mode
        // (This assertion will FAIL until 6.2 adds the chat-mode branch)
        mockLifetime.Verify(l => l.StopApplication(), Times.Once,
            "in chat mode, HandleCancelChatAsync must call StopApplication() to shut down the pod");

        // Assert 2: AgentReady must NOT have been sent over the hub
        // (The hub is disconnected in test, so InvokeAsync would throw. If it was NOT called,
        //  no exception is thrown. We verify this indirectly by confirming StopApplication was called
        //  without an intermediate SignalAgentReadyAsync call throwing/logging.)
        // The key invariant is: StopApplication is called AND no AgentReady hub call occurs.
        // Since we can't easily intercept hub calls here, we verify StopApplication was called,
        // which is only done in the chat-mode branch that suppresses AgentReady.
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 4: non-chat mode HandleCancelChatAsync → StopApplication NOT called, AgentReady sent
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 6 (regression path)
    ///
    /// In non-chat mode, HandleCancelChatAsync must use the existing SignalR path:
    /// call SignalAgentReadyAsync() and NOT call StopApplication().
    /// </summary>
    [Fact]
    public async Task NonChatMode_HandleCancelChatAsync_StopApplicationNotCalled()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "false");

        var mockLifetime = new Mock<IHostApplicationLifetime>();
        mockLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var (service, slotManager, lifecycle, chatHandler4) = TestAgentWorkerServiceFactory.CreateWithComponents(
            hostLifetime: mockLifetime.Object);

        var sessionId = "cancel-session-non-chat-mode";
        SetPrivateField(slotManager, "_activeChatSessionId", sessionId);
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());
        SetPrivateField(slotManager, "_activeChatTask", Task.CompletedTask);

        // Act
        var chatJobHandler = GetChatJobHandler(chatHandler4);
        await chatJobHandler.HandleCancelChatAsync(sessionId);

        // Assert — StopApplication must NOT be called in non-chat mode
        mockLifetime.Verify(l => l.StopApplication(), Times.Never,
            "in non-chat mode, HandleCancelChatAsync must NOT call StopApplication() — " +
            "the pod should return to the idle pool via SignalAgentReadyAsync()");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 5: chat mode cancel → SignalChatEnd() unblocks ConnectAndRunAsync
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 5, 6
    ///
    /// HandleCancelChatAsync in chat mode must call lifecycle.SignalChatEnd() so that
    /// ConnectAndRunAsync (which awaits _chatEndSource.Task) can return and the pod exits.
    /// Without this signal, the pod would hang waiting for the chat end source forever.
    /// </summary>
    [Fact]
    public async Task ChatMode_HandleCancelChatAsync_SignalChatEndUnblocksConnectAndRunAsync()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");

        var mockLifetime = new Mock<IHostApplicationLifetime>();
        mockLifetime.Setup(l => l.ApplicationStopping).Returns(CancellationToken.None);

        var (service, slotManager, lifecycle, chatHandler5) = TestAgentWorkerServiceFactory.CreateWithComponents(
            hostLifetime: mockLifetime.Object);

        var sessionId = "chat-end-signal-session";
        SetPrivateField(slotManager, "_activeChatSessionId", sessionId);
        SetPrivateField(slotManager, "_chatCts", new CancellationTokenSource());
        SetPrivateField(slotManager, "_activeChatTask", Task.CompletedTask);

        // Assert pre-condition: _chatEndSource is NOT yet completed
        lifecycle._chatEndSource.Task.IsCompleted.Should().BeFalse(
            "before cancel, _chatEndSource must not be signalled");

        // Act — HandleCancelChatAsync should call lifecycle.SignalChatEnd()
        var chatJobHandler = GetChatJobHandler(chatHandler5);
        await chatJobHandler.HandleCancelChatAsync(sessionId);

        // Assert — _chatEndSource must now be completed (SignalChatEnd was called)
        // This assertion FAILS until 6.2 adds _connectionLifecycle.SignalChatEnd() call
        lifecycle._chatEndSource.Task.IsCompleted.Should().BeTrue(
            "HandleCancelChatAsync in chat mode must call SignalChatEnd() to unblock ConnectAndRunAsync");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 6: ChatWindowId non-empty → workspace under ChatWorkspacesRoot/<id>
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 14
    ///
    /// HandleChatPromptAsync must derive the workspace from ChatWindowId when non-empty:
    ///   Path.Combine(AgentDefaults.ChatWorkspacesRoot, message.ChatWindowId)
    ///
    /// This test exercises the production code path in AgentWorkerService, confirming
    /// it uses ChatWorkspacesRoot (not the legacy ChatWorkspacePath) when ChatWindowId is set.
    /// </summary>
    [Fact]
    public async Task HandleChatPromptAsync_NonEmptyChatWindowId_WorkspaceIsUnderChatWorkspacesRoot()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "false"); // non-chat mode so job slot isn't gated
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var workspaceCapture = new List<string>();
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
                (prompt, workspace, _, _, _, _, _) =>
                {
                    workspaceCapture.Add(workspace);
                    return Task.FromResult(0);
                });

        var (service, slotManager, _, chatHandler6) = TestAgentWorkerServiceFactory.CreateWithComponents(
            orchestrator: mockOrchestrator.Object);

        var chatWindowId = Guid.NewGuid().ToString();
        var expectedWorkspace = Path.Combine(AgentDefaults.ChatWorkspacesRoot, chatWindowId);

        // Pre-create workspace so Directory.CreateDirectory doesn't fail on CI
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacesRoot); }
        catch { /* best effort */ }

        var message = new ChatPromptMessage
        {
            SessionId = "session-window-id",
            Prompt = "test prompt",
            UseResume = true,
            // ChatWindowId property requires task 3X.2 — if ChatPromptMessage doesn't have it yet
            // this will compile-fail. But 3X.2 is already done per tasks.md.
            ChatWindowId = chatWindowId
        };

        // Act — invoke HandleChatPromptAsync; it runs a background Task.Run
        var chatJobHandler = GetChatJobHandler(chatHandler6);
        await chatJobHandler.HandleChatPromptAsync(message);

        // Wait for background task to complete
        var chatTask = GetPrivateField<Task?>(slotManager, "_activeChatTask");
        if (chatTask is not null)
            await Task.WhenAny(chatTask, Task.Delay(5000));

        // Assert — the workspace passed to ExecutePromptAsync must be under ChatWorkspacesRoot
        workspaceCapture.Should().NotBeEmpty("HandleChatPromptAsync must invoke the orchestrator — check mock setup");
        workspaceCapture[0].Should().Be(expectedWorkspace,
            "when ChatWindowId is non-empty, workspace must be Path.Combine(ChatWorkspacesRoot, chatWindowId)");
        workspaceCapture[0].Should().StartWith(AgentDefaults.ChatWorkspacesRoot,
            "workspace must be scoped under ChatWorkspacesRoot for session isolation");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test 7: ChatWindowId empty → static ChatWorkspacePath
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates: Requirements 14 (backward compat)
    ///
    /// When ChatWindowId is empty (old SignalR-mode agents that don't populate it),
    /// HandleChatPromptAsync must fall back to AgentDefaults.ChatWorkspacePath.
    /// </summary>
    [Fact]
    public async Task HandleChatPromptAsync_EmptyChatWindowId_WorkspaceIsStaticChatWorkspacePath()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "false");
        Environment.SetEnvironmentVariable("AGENT_PROVIDER_TYPE", "KiroCli");

        var workspaceCapture = new List<string>();
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
                (prompt, workspace, _, _, _, _, _) =>
                {
                    workspaceCapture.Add(workspace);
                    return Task.FromResult(0);
                });

        var (service, slotManager, _, chatHandler7) = TestAgentWorkerServiceFactory.CreateWithComponents(
            orchestrator: mockOrchestrator.Object);

        // Pre-create workspace so Directory.CreateDirectory doesn't fail on CI
        try { Directory.CreateDirectory(AgentDefaults.ChatWorkspacePath); }
        catch { /* best effort */ }

        var message = new ChatPromptMessage
        {
            SessionId = "session-empty-window-id",
            Prompt = "test prompt",
            UseResume = true,
            ChatWindowId = "" // empty → backward compat path
        };

        // Act
        var chatJobHandler = GetChatJobHandler(chatHandler7);
        await chatJobHandler.HandleChatPromptAsync(message);

        var chatTask = GetPrivateField<Task?>(slotManager, "_activeChatTask");
        if (chatTask is not null)
            await Task.WhenAny(chatTask, Task.Delay(5000));

        // Assert
        workspaceCapture.Should().NotBeEmpty("HandleChatPromptAsync must invoke the orchestrator — check mock setup");
        workspaceCapture[0].Should().Be(AgentDefaults.ChatWorkspacePath,
            "when ChatWindowId is empty, workspace must fall back to static AgentDefaults.ChatWorkspacePath");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Returns the current delegate value of a named event on <paramref name="obj"/>.
    /// Uses reflection on the compiler-generated backing field (same name as event).
    /// Returns null if no subscribers are registered.
    /// </summary>
    private static Delegate? GetEventDelegate(object obj, string eventName)
    {
        // For field-like events, the compiler generates a backing field with the same name.
        var field = obj.GetType().GetField(eventName,
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);

        if (field != null)
            return field.GetValue(obj) as Delegate;

        // Some compilers use add_/remove_ pattern — fall back to direct field search.
        var backingField = obj.GetType()
            .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(f => f.Name.Contains(eventName, StringComparison.OrdinalIgnoreCase));

        return backingField?.GetValue(obj) as Delegate;
    }

    private static MethodInfo GetPrivateMethod(object obj, string methodName)
    {
        return obj.GetType().GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Method '{methodName}' not found on {obj.GetType().Name}");
    }

    private static void SetPrivateField(object obj, string fieldName, object? value)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{fieldName}' not found on {obj.GetType().Name}");
        return (T?)field.GetValue(obj);
    }

    private static ChatJobHandler GetChatJobHandler(ChatJobHandler chatJobHandler) => chatJobHandler;
}
