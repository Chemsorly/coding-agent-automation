using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Tests.Properties;

/// <summary>
/// Property 8: Concurrent Execution Guard
/// Feature: kiro-web-ui, Property 8: Concurrent Execution Guard
/// Validates: Requirements 12.1, 12.2
/// </summary>
public class ConcurrentExecutionPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    /// <summary>
    /// Property 8: When an execution is in progress, a second concurrent call
    /// throws InvalidOperationException without invoking orchestrator a second time,
    /// and the first execution completes normally.
    /// </summary>
    [Fact]
    public async Task ConcurrentExecution_IsRejected_WithoutAffectingFirstExecution()
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        var executionStarted = new TaskCompletionSource<bool>();
        var allowCompletion = new TaskCompletionSource<bool>();
        var invocationCount = 0;

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Returns<string, string, bool, CancellationToken, Action<string>?>(async (prompt, dir, useResume, ct, onOutput) =>
            {
                Interlocked.Increment(ref invocationCount);
                executionStarted.TrySetResult(true);
                await allowCompletion.Task;
                return 0;
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            _ => mockOrchestrator.Object);

        // Start first execution
        var firstExecution = service.ExecutePromptAsync("first prompt", CancellationToken.None);

        // Wait for first execution to actually start
        await executionStarted.Task;

        // Attempt second concurrent execution — should throw
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.ExecutePromptAsync("second prompt", CancellationToken.None));

        Assert.Contains("already in progress", ex.Message);

        // Orchestrator was only invoked once
        Assert.Equal(1, invocationCount);

        // Allow first execution to complete
        allowCompletion.SetResult(true);
        var result = await firstExecution;

        // First execution completed normally
        Assert.Equal(0, result.ExitCode);

        service.Dispose();
    }

    /// <summary>
    /// Property 8 (multiple attempts): Run multiple concurrent attempts to verify
    /// the guard holds consistently.
    /// </summary>
    [Fact]
    public async Task ConcurrentExecution_MultipleAttempts_AllRejected()
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        var executionStarted = new TaskCompletionSource<bool>();
        var allowCompletion = new TaskCompletionSource<bool>();

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Returns<string, string, bool, CancellationToken, Action<string>?>(async (prompt, dir, useResume, ct, onOutput) =>
            {
                executionStarted.TrySetResult(true);
                await allowCompletion.Task;
                return 0;
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            _ => mockOrchestrator.Object);

        // Start first execution
        var firstExecution = service.ExecutePromptAsync("first", CancellationToken.None);
        await executionStarted.Task;

        // Attempt 5 concurrent executions — all should throw
        var concurrentTasks = Enumerable.Range(0, 5)
            .Select(i => Assert.ThrowsAsync<InvalidOperationException>(
                () => service.ExecutePromptAsync($"concurrent-{i}", CancellationToken.None)))
            .ToList();

        await Task.WhenAll(concurrentTasks);

        // Allow first to complete
        allowCompletion.SetResult(true);
        var result = await firstExecution;
        Assert.Equal(0, result.ExitCode);

        service.Dispose();
    }
}
