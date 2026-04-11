using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Services;
using Moq;
using Xunit;
using ILogger = Serilog.ILogger;

namespace KiroCliPoc.Tests.Properties;

/// <summary>
/// Property 2: Whitespace Prompt Rejection (Chat Component)
/// Feature: kiro-web-ui, Property 2: Whitespace Prompt Rejection
/// Validates: Requirements 3.4
/// </summary>
public class InputValidationPropertyTests
{
    private static Configuration CreateTestConfig() => new()
    {
        WorkspaceDirectory = ".",
        KiroCliPath = "/usr/bin/kiro-cli",
        UseWsl = false
    };

    public static class Generators
    {
        public static Arbitrary<string> WhitespaceString()
        {
            var whitespaceChars = new[] { ' ', '\t', '\n', '\r' };
            var gen = Gen.Choose(0, 20)
                .SelectMany(len =>
                    Gen.Elements(whitespaceChars).ArrayOf(len)
                        .Select(chars => new string(chars)));
            return gen.ToArbitrary();
        }
    }

    /// <summary>
    /// Property 2: Any whitespace-only string is rejected without invoking orchestrator,
    /// and the message list remains unchanged.
    /// </summary>
    [Property(MaxTest = 100, Arbitrary = new[] { typeof(Generators) })]
    public void WhitespacePrompt_IsRejected_WithoutInvokingOrchestrator(string whitespacePrompt)
    {
        var config = CreateTestConfig();
        var mockLogger = new Mock<ILogger>();
        var mockOrchestrator = new Mock<IKiroCliOrchestrator>();

        var service = new KiroExecutionService(config, mockLogger.Object,
            _ => mockOrchestrator.Object);

        var messagesBefore = service.Messages.Count;

        var ex = Assert.ThrowsAsync<ArgumentException>(
            () => service.ExecutePromptAsync(whitespacePrompt, CancellationToken.None))
            .GetAwaiter().GetResult();

        Assert.Contains("Prompt cannot be empty", ex.Message);

        // Orchestrator was never called
        mockOrchestrator.Verify(
            o => o.ExecutePromptAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<string>?>()),
            Times.Never);

        // Messages unchanged
        Assert.Equal(messagesBefore, service.Messages.Count);

        service.Dispose();
    }
}
