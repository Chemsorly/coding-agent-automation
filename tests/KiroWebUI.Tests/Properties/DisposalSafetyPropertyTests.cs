using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Tests.Properties;

/// <summary>
/// Property 10: Disposal Safety
/// Feature: kiro-web-ui, Property 10: Disposal Safety
/// Validates: Requirements 10.4, 10.5
/// </summary>
public class DisposalSafetyPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    /// <summary>
    /// Property 10: After disposal, callbacks don't throw, don't mutate state,
    /// and don't raise events. In-flight execution is cancelled.
    /// </summary>
    [Fact]
    public async Task DisposeDuringExecution_CancelsExecution_CallbacksSafe()
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        var executionStarted = new TaskCompletionSource<bool>();
        var outputCallbackFired = false;
        var stateChangeFired = false;
        var onChangeFired = false;

        CallbackHandler? capturedHandler = null;

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

                // Wait for cancellation (from disposal)
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    // After disposal, try invoking callbacks — they should be safe
                    onOutput?.Invoke("post-disposal line");

                    capturedHandler?.Invoke(KiroState.Error, new CallbackContext
                    {
                        State = KiroState.Error,
                        Message = "post-disposal state change"
                    });

                    throw;
                }

                return 0;
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            handler =>
            {
                capturedHandler = handler;
                return mockOrchestrator.Object;
            });

        // Subscribe to events to track if they fire after disposal
        service.OnOutputLineReceived += _ => outputCallbackFired = true;
        service.OnStateChanged += _ => stateChangeFired = true;
        service.OnChange += () => onChangeFired = true;

        // Start execution
        var executionTask = service.ExecutePromptAsync("test", CancellationToken.None);

        // Wait for execution to start
        await executionStarted.Task;

        // Reset flags (events may have fired during startup)
        outputCallbackFired = false;
        stateChangeFired = false;
        onChangeFired = false;

        // Dispose the service mid-execution
        service.Dispose();

        // The execution should complete (with cancellation)
        var result = await executionTask;

        // After disposal, events should NOT have fired (events nulled in Dispose)
        Assert.False(outputCallbackFired, "OnOutputLineReceived should not fire after disposal");
        Assert.False(stateChangeFired, "OnStateChanged should not fire after disposal");
        Assert.False(onChangeFired, "OnChange should not fire after disposal");

        // Exit code should indicate cancellation
        Assert.Equal(130, result.ExitCode);
    }

    /// <summary>
    /// Property 10: Disposing a service that is not executing should be safe.
    /// </summary>
    [Fact]
    public void DisposeWithoutExecution_IsSafe()
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();

        var service = new KiroExecutionService(config, mockLogger.Object);

        // Should not throw
        service.Dispose();

        // Double dispose should also be safe
        service.Dispose();
    }
}
