using FsCheck;
using FsCheck.Xunit;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Models;
using KiroWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace KiroCliPoc.Tests.Properties;

/// <summary>
/// Property 1: Session State Machine (Resume Flag Correctness)
/// Feature: kiro-web-ui, Property 1: Session State Machine
/// Validates: Requirements 3.2, 3.3, 6.1, 6.2, 6.3, 6.4
/// </summary>
public class SessionStatePropertyTests
{
    /// <summary>
    /// Represents a single action in a session sequence.
    /// </summary>
    public record SessionAction(string Prompt, int ExitCode, bool ClearAfter);

    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    /// <summary>
    /// Property 1: For any sequence of prompts with optional clear-session actions,
    /// useResume is false after init/clear, true only after exit code 0.
    /// </summary>
    [Property(MaxTest = 100)]
    public void ResumeFlag_FollowsSessionStateMachine(byte[] actionSeeds)
    {
        if (actionSeeds == null || actionSeeds.Length == 0) return;

        // Build actions from seeds
        var actions = actionSeeds.Select(seed => new SessionAction(
            Prompt: $"prompt-{seed}",
            ExitCode: seed % 4 == 0 ? 1 : 0, // ~25% failure rate
            ClearAfter: seed % 7 == 0          // ~14% clear rate
        )).Take(10).ToList(); // Cap at 10 to keep tests fast

        var capturedResumeFlags = new List<bool>();
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();

        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        // Track which action index we're on
        var actionIndex = 0;

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Returns<string, string, bool, CancellationToken, Action<string>?>((prompt, dir, useResume, ct, onOutput) =>
            {
                capturedResumeFlags.Add(useResume);
                var idx = actionIndex++;
                return Task.FromResult(actions[idx].ExitCode);
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            _ => mockOrchestrator.Object);

        // Execute actions and track expected resume state
        var expectedResumeFlags = new List<bool>();
        var lastExitCodeWasZero = false;

        for (var i = 0; i < actions.Count; i++)
        {
            expectedResumeFlags.Add(lastExitCodeWasZero);

            var result = service.ExecutePromptAsync(actions[i].Prompt, CancellationToken.None).GetAwaiter().GetResult();

            lastExitCodeWasZero = actions[i].ExitCode == 0;

            if (actions[i].ClearAfter)
            {
                service.ClearSession();
                lastExitCodeWasZero = false;
            }
        }

        // Assert all captured resume flags match expected
        Assert.Equal(expectedResumeFlags.Count, capturedResumeFlags.Count);
        for (var i = 0; i < expectedResumeFlags.Count; i++)
        {
            Assert.Equal(expectedResumeFlags[i], capturedResumeFlags[i]);
        }

        service.Dispose();
    }
}
