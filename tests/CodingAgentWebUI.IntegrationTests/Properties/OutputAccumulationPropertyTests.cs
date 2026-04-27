using FsCheck;
using FsCheck.Xunit;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.IntegrationTests.Properties;

/// <summary>
/// Property 4: Output Line Accumulation
/// Feature: kiro-web-ui, Property 4: Output Line Accumulation
/// Validates: Requirements 5.1, 7.3
/// </summary>
public class OutputAccumulationPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    /// <summary>
    /// Property 4: For any sequence of output lines, the assistant ChatMessage.Content
    /// contains all lines in exact order, none missing or reordered.
    /// </summary>
    [Property(MaxTest = 20)]
    public void OutputLines_AreAccumulated_InOrder(NonEmptyArray<NonEmptyString> outputLineValues)
    {
        var outputLines = outputLineValues.Get
            .Select(s => s.Get.Replace("\n", " ").Replace("\r", " "))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Where(s => !string.IsNullOrWhiteSpace(CodingAgentWebUI.Pipeline.Services.AnsiStripper.Strip(s))) // skip lines that become empty after ANSI stripping
            .Take(50)
            .ToList();

        if (outputLines.Count == 0) return;

        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()))
            .Returns<string, string, bool, CancellationToken, Action<string>?>((prompt, dir, useResume, ct, onOutput) =>
            {
                // Simulate orchestrator producing output lines
                foreach (var line in outputLines)
                {
                    onOutput?.Invoke(line);
                }
                return Task.FromResult(0);
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            _ => mockOrchestrator.Object);

        var result = service.ExecutePromptAsync("test prompt", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Find the assistant message
        var assistantMessage = service.Messages.FirstOrDefault(m => m.Role == ChatMessageRole.Assistant);
        Assert.NotNull(assistantMessage);

        // Verify all lines are present in order (after ANSI stripping)
        var content = assistantMessage.Content;
        var contentLines = content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var expectedLines = outputLines.Select(CodingAgentWebUI.Pipeline.Services.AnsiStripper.Strip).Where(l => !string.IsNullOrEmpty(l)).ToList();

        Assert.Equal(expectedLines.Count, contentLines.Length);
        for (var i = 0; i < expectedLines.Count; i++)
        {
            Assert.Equal(expectedLines[i], contentLines[i]);
        }

        // Also verify the ExecutionResult output lines (also ANSI-stripped)
        Assert.Equal(expectedLines.Count, result.OutputLines.Count);
        for (var i = 0; i < expectedLines.Count; i++)
        {
            Assert.Equal(expectedLines[i], result.OutputLines[i]);
        }

        service.Dispose();
    }
}
