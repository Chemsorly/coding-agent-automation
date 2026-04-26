using FsCheck.Xunit;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.IntegrationTests.Properties;

/// <summary>
/// Property 5: State Transition Propagation
/// Feature: kiro-web-ui, Property 5: State Transition Propagation
/// Validates: Requirements 5.2, 7.4
/// </summary>
public class StateTransitionPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    /// <summary>
    /// Property 5: For any KiroState values emitted via CallbackHandler,
    /// CurrentState equals the last emitted state after each callback.
    /// </summary>
    [Property(MaxTest = 20)]
    public void CurrentState_EqualsLastEmittedState(byte[] stateSeeds)
    {
        if (stateSeeds == null || stateSeeds.Length == 0) return;

        var allStates = Enum.GetValues<KiroState>();
        var stateSequence = stateSeeds
            .Select(s => allStates[s % allStates.Length])
            .Take(20)
            .ToList();

        if (stateSequence.Count == 0) return;

        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        // Capture the CallbackHandler so we can invoke state changes
        CallbackHandler? capturedHandler = null;

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Returns<string, string, bool, CancellationToken, Action<string>?>((prompt, dir, useResume, ct, onOutput) =>
            {
                // Simulate state transitions using the captured handler
                foreach (var state in stateSequence)
                {
                    capturedHandler!.Invoke(state, new CallbackContext { State = state });
                }
                return Task.FromResult(0);
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            handler =>
            {
                capturedHandler = handler;
                return mockOrchestrator.Object;
            });

        service.ExecutePromptAsync("test", CancellationToken.None).GetAwaiter().GetResult();

        // CurrentState should equal the last state in the sequence
        var lastEmitted = stateSequence.Last();
        Assert.Equal(lastEmitted, service.CurrentState);

        service.Dispose();
    }
}
