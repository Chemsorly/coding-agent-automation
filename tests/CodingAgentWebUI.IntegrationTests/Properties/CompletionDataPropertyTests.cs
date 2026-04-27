using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using CodingAgentWebUI.Models;
using CodingAgentWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;
using TestResult = KiroCliLib.Models.TestResult;

namespace CodingAgentWebUI.IntegrationTests.Properties;

/// <summary>
/// Property 6: Completion Data Propagation
/// Feature: kiro-web-ui, Property 6: Completion Data Propagation
/// Validates: Requirements 5.3, 5.4
/// </summary>
public class CompletionDataPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    public record CompletionScenario(
        int ExitCode,
        bool HasFileChanges,
        int FileChangeCount,
        bool HasTestResults,
        int PassedTests,
        int FailedTests);

    public static class Generators
    {
        public static Arbitrary<CompletionScenario> CompletionScenario()
        {
            var gen = from exitCode in Gen.Choose(-1, 130)
                      from hasFileChanges in Gen.Elements(true, false)
                      from fileChangeCount in Gen.Choose(0, 10)
                      from hasTestResults in Gen.Elements(true, false)
                      from passed in Gen.Choose(0, 50)
                      from failed in Gen.Choose(0, 10)
                      select new CompletionScenario(
                          exitCode, hasFileChanges, fileChangeCount,
                          hasTestResults, passed, failed);
            return gen.ToArbitrary();
        }
    }

    /// <summary>
    /// Property 6: For any execution, the assistant ChatMessage contains correct
    /// exit code, file changes when present, test results when present.
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(Generators) })]
    public void CompletionData_IsPropagated_ToAssistantMessage(CompletionScenario scenario)
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        // Build file changes
        var fileChanges = scenario.HasFileChanges && scenario.FileChangeCount > 0
            ? Enumerable.Range(0, scenario.FileChangeCount)
                .Select(i => new FileChange
                {
                    Path = $"src/file{i}.cs",
                    Type = (FileChangeType)(i % 3)
                })
                .ToList()
                .AsReadOnly()
            : null;

        var testResults = scenario.HasTestResults
            ? new TestResult
            {
                TotalTests = scenario.PassedTests + scenario.FailedTests,
                PassedTests = scenario.PassedTests,
                FailedTests = scenario.FailedTests
            }
            : null;

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
                // Simulate completion callback with file changes and test results
                capturedHandler!.Invoke(KiroState.Completed, new CallbackContext
                {
                    State = KiroState.Completed,
                    FileChanges = fileChanges,
                    TestResults = testResults,
                    ExitCode = scenario.ExitCode
                });
                return Task.FromResult(scenario.ExitCode);
            });

        var service = new KiroExecutionService(config, mockLogger.Object,
            handler =>
            {
                capturedHandler = handler;
                return mockOrchestrator.Object;
            });

        var result = service.ExecutePromptAsync("test prompt", CancellationToken.None)
            .GetAwaiter().GetResult();

        // Find assistant message
        var assistantMessage = service.Messages.FirstOrDefault(m => m.Role == ChatMessageRole.Assistant);
        Assert.NotNull(assistantMessage);

        // Verify exit code
        Assert.Equal(scenario.ExitCode, assistantMessage.ExitCode);
        Assert.Equal(scenario.ExitCode, result.ExitCode);

        // Verify file changes
        if (fileChanges != null && fileChanges.Count > 0)
        {
            Assert.NotNull(assistantMessage.FileChanges);
            Assert.Equal(fileChanges.Count, assistantMessage.FileChanges!.Count);
            for (var i = 0; i < fileChanges.Count; i++)
            {
                Assert.Equal(fileChanges[i].Path, assistantMessage.FileChanges[i].Path);
                Assert.Equal(fileChanges[i].Type, assistantMessage.FileChanges[i].Type);
            }
        }
        else
        {
            Assert.True(assistantMessage.FileChanges == null || assistantMessage.FileChanges.Count == 0);
        }

        // Verify test results
        if (testResults != null)
        {
            Assert.NotNull(assistantMessage.TestResults);
            Assert.Equal(testResults.TotalTests, assistantMessage.TestResults!.TotalTests);
            Assert.Equal(testResults.PassedTests, assistantMessage.TestResults.PassedTests);
            Assert.Equal(testResults.FailedTests, assistantMessage.TestResults.FailedTests);
        }
        else
        {
            Assert.Null(assistantMessage.TestResults);
        }

        service.Dispose();
    }
}
