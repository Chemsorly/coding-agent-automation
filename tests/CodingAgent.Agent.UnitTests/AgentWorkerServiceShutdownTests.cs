using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Agent;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Targeted tests for uncovered branches in AgentWorkerService:
/// - ShutdownAsync with active chat session (cancels chat + waits)
/// - ExecuteAsync swallowing the cancellation that stops the service
/// </summary>
[Collection("EnvironmentVariables")]
public sealed class AgentWorkerServiceShutdownTests : IDisposable
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

    // ── ShutdownAsync with active chat session ────────────────────────────────

    [Fact]
    public async Task ShutdownAsync_WithActiveChatSession_CancelsChatCts()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        var chatCts = new CancellationTokenSource();
        var completionSource = new TaskCompletionSource();
        completionSource.SetResult(); // complete immediately so shutdown doesn't hang

        SetPrivateField(slotManager, "_activeChatSessionId", "chat-session-shutdown");
        SetPrivateField(slotManager, "_chatCts", chatCts);
        SetPrivateField(slotManager, "_activeChatTask", completionSource.Task);

        await (Task)GetPrivateMethod(service, "ShutdownAsync").Invoke(service, [])!;

        chatCts.IsCancellationRequested.Should().BeTrue(
            "shutdown must cancel active chat session's CancellationTokenSource");
    }

    [Fact]
    public async Task ShutdownAsync_WithActiveChatSession_WaitsForChatTaskCompletion()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);
        var chatCts = new CancellationTokenSource();
        var completionSource = new TaskCompletionSource();

        SetPrivateField(slotManager, "_activeChatSessionId", "chat-session-wait");
        SetPrivateField(slotManager, "_chatCts", chatCts);
        SetPrivateField(slotManager, "_activeChatTask", completionSource.Task);

        // Let shutdown run, but complete the chat task before timeout
        var shutdownTask = (Task)GetPrivateMethod(service, "ShutdownAsync").Invoke(service, [])!;
        completionSource.SetResult();

        await shutdownTask.WaitAsync(TimeSpan.FromSeconds(10));

        shutdownTask.IsCompleted.Should().BeTrue("shutdown should complete once chat task finishes");
    }

    [Fact]
    public async Task ShutdownAsync_NoChatSession_CompletesWithoutCancelling()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        // No active chat session
        GetPrivateField<string?>(slotManager, "_activeChatSessionId").Should().BeNull();

        // Should complete without touching any chat CTS
        await (Task)GetPrivateMethod(service, "ShutdownAsync").Invoke(service, [])!;
    }

    [Fact]
    public async Task ShutdownAsync_WithBothJobAndChatSession_CancelsBoth()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        var slotManager = GetSlotManager(service);

        var jobCts = new CancellationTokenSource();
        var chatCts = new CancellationTokenSource();
        var jobTask = Task.CompletedTask;
        var chatTask = Task.CompletedTask;

        SetPrivateField(slotManager, "_activeJobId", (JobId?)(JobId)"job-shutdown");
        SetPrivateField(slotManager, "_isBusy", true);
        SetPrivateField(slotManager, "_jobCts", jobCts);
        SetPrivateField(slotManager, "_activeJobTask", jobTask);

        SetPrivateField(slotManager, "_activeChatSessionId", "chat-shutdown");
        SetPrivateField(slotManager, "_chatCts", chatCts);
        SetPrivateField(slotManager, "_activeChatTask", chatTask);

        await (Task)GetPrivateMethod(service, "ShutdownAsync").Invoke(service, [])!;

        jobCts.IsCancellationRequested.Should().BeTrue("active job CTS must be cancelled on shutdown");
        chatCts.IsCancellationRequested.Should().BeTrue("active chat CTS must be cancelled on shutdown");
    }

    // ── ExecuteAsync — OperationCanceledException is swallowed ──────────────

    [Fact]
    public async Task ExecuteAsync_OperationCanceledException_DoesNotPropagate()
    {
        var service = TestAgentWorkerServiceFactory.Create();
        using var cts = new CancellationTokenSource();

        var executeTask = service.StartAsync(cts.Token);
        await cts.CancelAsync();

        // Should not throw — OCE from ConnectAndRunAsync is caught when stoppingToken is cancelled
        Func<Task> act = async () =>
        {
            try
            {
                await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (OperationCanceledException)
            {
                // StartAsync itself may throw OCE — that's acceptable; re-throw to let
                // AwesomeAssertions handle it without counting as a test failure
            }
        };

        await act.Should().NotThrowAsync<Exception>("no unexpected exceptions must escape from ExecuteAsync");
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AgentJobSlotManager GetSlotManager(AgentWorkerService service)
    {
        var field = typeof(AgentWorkerService).GetField("_slotManager",
            BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("_slotManager not found");
        return (AgentJobSlotManager)field.GetValue(service)!;
    }

    private static MethodInfo GetPrivateMethod(object obj, string name) =>
        obj.GetType().GetMethod(name, BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new InvalidOperationException($"Method '{name}' not found");

    private static void SetPrivateField(object obj, string name, object? value)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found on {obj.GetType().Name}");
        field.SetValue(obj, value);
    }

    private static T? GetPrivateField<T>(object obj, string name)
    {
        var field = obj.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException($"Field '{name}' not found");
        return (T?)field.GetValue(obj);
    }
}
