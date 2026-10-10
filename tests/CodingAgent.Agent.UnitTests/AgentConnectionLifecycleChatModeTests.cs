using AwesomeAssertions;
using CodingAgent.Pipeline;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for chat-mode detection, label extension and SignalChatEnd() in
/// <see cref="AgentConnectionLifecycle"/> (Req 5, Req 16).
/// </summary>
/// <remarks>
/// Validates: Requirements 5, 16
/// </remarks>
[Collection("EnvironmentVariables")]
public class AgentConnectionLifecycleChatModeTests : IDisposable
{
    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a lifecycle under test with the given env var overrides.
    /// Env vars are set before construction (constructor reads them) and
    /// cleared in Dispose().
    /// </summary>
    private readonly List<string> _setEnvVars = [];

    private static AgentConnectionLifecycle CreateLifecycle(
        IHostApplicationLifetime? hostLifetime = null,
        Serilog.ILogger? logger = null)
    {
        var (_, _, lifecycle, _) = TestAgentWorkerServiceFactory.CreateWithComponents(
            hostLifetime: hostLifetime,
            logger: logger);
        return lifecycle;
    }

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
        foreach (var key in _setEnvVars.ToList())
            Environment.SetEnvironmentVariable(key, null);
        GC.SuppressFinalize(this);
    }

    // ── Test 1: AGENT_CHAT_MODE=true → labels include "chat=true" and "chat-session-id=<id>" ──

    [Fact]
    public void ChatModeTrue_LabelsIncludeChatTrueAndSessionId()
    {
        // Arrange
        var sessionId = Guid.NewGuid().ToString();
        SetEnv("AGENT_CHAT_MODE", "true");
        SetEnv("AGENT_CHAT_SESSION_ID", sessionId);

        // Act — constructor sets _isChatMode; BuildRegistrationMessage() reads it
        var lifecycle = CreateLifecycle();
        var registration = lifecycle.BuildRegistrationMessageForTest();

        // Assert
        registration.Labels.Should().Contain("chat=true");
        registration.Labels.Should().Contain($"chat-session-id={sessionId}");
    }

    [Fact]
    public void ChatModeTrue_IsChatModeIsTrue()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");

        // Act
        var lifecycle = CreateLifecycle();

        // Assert — _isChatMode is true when env var is "true" (case-insensitive)
        lifecycle._isChatMode.Should().BeTrue();
    }

    // ── Test 2: AGENT_CHAT_MODE=TRUE (uppercase) → also includes labels ──

    [Fact]
    public void ChatModeUppercase_LabelsIncludeChatTrueAndSessionId()
    {
        // Arrange
        var sessionId = Guid.NewGuid().ToString();
        SetEnv("AGENT_CHAT_MODE", "TRUE");
        SetEnv("AGENT_CHAT_SESSION_ID", sessionId);

        // Act
        var lifecycle = CreateLifecycle();
        var registration = lifecycle.BuildRegistrationMessageForTest();

        // Assert — OrdinalIgnoreCase means TRUE == true
        registration.Labels.Should().Contain("chat=true");
        registration.Labels.Should().Contain($"chat-session-id={sessionId}");
    }

    [Fact]
    public void ChatModeMixedCase_IsChatModeIsTrue()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "True");

        // Act
        var lifecycle = CreateLifecycle();

        // Assert
        lifecycle._isChatMode.Should().BeTrue();
    }

    // ── Test 3: AGENT_CHAT_MODE=false / absent → no "chat=" label ──

    [Fact]
    public void ChatModeFalse_NoChatlabels()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "false");

        // Act
        var lifecycle = CreateLifecycle();
        var registration = lifecycle.BuildRegistrationMessageForTest();

        // Assert
        registration.Labels.Should().NotContain(l => l.StartsWith("chat="));
        lifecycle._isChatMode.Should().BeFalse();
    }

    [Fact]
    public void ChatModeAbsent_NoChatlabels()
    {
        // Arrange — ensure not set
        UnsetEnv("AGENT_CHAT_MODE");

        // Act
        var lifecycle = CreateLifecycle();
        var registration = lifecycle.BuildRegistrationMessageForTest();

        // Assert
        registration.Labels.Should().NotContain(l => l.StartsWith("chat="));
        lifecycle._isChatMode.Should().BeFalse();
    }

    // ── Test 4: absent env var → no NullReferenceException ──

    [Fact]
    public void AllChatEnvVarsAbsent_DoesNotThrow()
    {
        // Arrange
        UnsetEnv("AGENT_CHAT_MODE");
        UnsetEnv("AGENT_CHAT_SESSION_ID");

        // Act
        var act = () =>
        {
            var lifecycle = CreateLifecycle();
            _ = lifecycle.BuildRegistrationMessageForTest();
        };

        // Assert — must not throw NullReferenceException when env vars are absent
        act.Should().NotThrow();
    }

    // ── Test 5: AGENT_CHAT_SESSION_ID absent → empty-value label ──

    [Fact]
    public void ChatModeTrue_SessionIdAbsent_EmptyValueLabelPresent()
    {
        // Arrange — AGENT_CHAT_MODE=true but AGENT_CHAT_SESSION_ID not set
        SetEnv("AGENT_CHAT_MODE", "true");
        UnsetEnv("AGENT_CHAT_SESSION_ID");

        // Act
        var lifecycle = CreateLifecycle();
        var registration = lifecycle.BuildRegistrationMessageForTest();

        // Assert — label "chat-session-id=" (with empty value) is present
        // Design: _chatSessionId = Environment.GetEnvironmentVariable("AGENT_CHAT_SESSION_ID") ?? ""
        registration.Labels.Should().Contain("chat-session-id=");
        registration.Labels.Should().Contain("chat=true");
    }

    // ── Test 6: SignalChatEnd() resolves _chatEndSource; idempotent on second call ──

    [Fact]
    public void SignalChatEnd_ResolvesChatEndSource()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");
        var lifecycle = CreateLifecycle();

        // Act
        lifecycle.SignalChatEnd();

        // Assert — the underlying TaskCompletionSource must be completed
        lifecycle._chatEndSource.Task.IsCompleted.Should().BeTrue();
    }

    [Fact]
    public void SignalChatEnd_Idempotent_SecondCallDoesNotThrow()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");
        var lifecycle = CreateLifecycle();

        // Act — call twice
        lifecycle.SignalChatEnd();
        var act = () => lifecycle.SignalChatEnd();

        // Assert — TrySetResult() is used (not SetResult()) so the second call is a no-op
        act.Should().NotThrow();
        lifecycle._chatEndSource.Task.IsCompleted.Should().BeTrue();
    }

    // ── Test 7: pre-cancelled stoppingToken → ConnectAndRunAsync returns without throwing ──

    [Fact]
    public async Task ConnectAndRunAsync_PreCancelledToken_HandledGracefully()
    {
        // Arrange
        SetEnv("AGENT_CHAT_MODE", "true");
        SetEnv("AGENT_CHAT_SESSION_ID", Guid.NewGuid().ToString());

        var lifecycle = CreateLifecycle();
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        // Act + Assert: ConnectAndRunAsync must not propagate unexpected exception types
        // (NullReferenceException, ObjectDisposedException, etc.) when given a pre-cancelled token.
        // Acceptable outcomes: returns cleanly, throws OperationCanceledException, or
        // throws HttpRequestException (no server at localhost in test env).
        Exception? unexpected = null;
        try
        {
            await lifecycle.ConnectAndRunAsync(cts.Token);
        }
        catch (OperationCanceledException) { /* expected — pre-cancelled token */ }
        catch (System.Net.Http.HttpRequestException) { /* expected — no server in test env */ }
        catch (Exception ex)
        {
            unexpected = ex;
        }

        unexpected.Should().BeNull(
            "pre-cancelled token must not propagate unexpected exception types; " +
            $"got: {unexpected?.GetType().Name}: {unexpected?.Message}");
    }
}
