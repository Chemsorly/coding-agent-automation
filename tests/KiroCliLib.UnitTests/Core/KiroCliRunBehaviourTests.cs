using AwesomeAssertions;
using KiroCliLib.Core;
using Moq;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Run arguments, cancellation and output parsing of one kiro-cli run.
/// </summary>
public class KiroCliRunBehaviourTests
{
    private static global::KiroCliLib.Configuration.Configuration Config(string? model = null, string? agent = null) =>
        new() { Model = model, AgentName = agent };

    [Fact]
    public void BuildArguments_ModelAgentAndResume_InOrder()
    {
        ProcessWrapper.BuildArguments(Config("claude-sonnet-4.6", "reviewer"), "--resume-id abc", "@.agent/p.md")
            .Should().Be("chat --agent-engine v2 --no-interactive --model \"claude-sonnet-4.6\" --agent \"reviewer\" --resume-id abc --trust-all-tools \"@.agent/p.md\"");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("AUTO")]
    public void BuildArguments_NoExplicitModelOrAgent_LeavesThemToTheCli(string? model)
    {
        ProcessWrapper.BuildArguments(Config(model, "  "), null, "@.agent/p.md")
            .Should().Be("chat --agent-engine v2 --no-interactive --trust-all-tools \"@.agent/p.md\"");
    }

    [Theory]
    [InlineData("reviewer", "\"reviewer\"")]
    [InlineData("my \"agent\"", "\"my \\\"agent\\\"\"")]
    [InlineData(@"agents\", "\"agents\\\\\"")]          // a trailing backslash must not escape the closing quote
    [InlineData(@"a\""b", "\"a\\\\\\\"b\"")]           // backslash before a quote: doubled, plus the quote's escape
    [InlineData(@"C:\dir\x", "\"C:\\dir\\x\"")]        // other backslashes stay literal
    public void Quote_FollowsTheArgumentSplittingRules(string value, string expected)
    {
        ProcessWrapper.Quote(value).Should().Be(expected);
    }

    [Fact]
    public async Task ExecutePromptAsync_Cancelled_Rethrows_SoATimeoutIsSeenAsOne()
    {
        var process = new Mock<IProcessWrapper>();
        process.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ThrowsAsync(new OperationCanceledException());
        var orchestrator = new KiroCliOrchestrator(Config(), new Mock<ILogger>().Object, () => process.Object);

        var act = () => orchestrator.ExecutePromptAsync("p", "/ws", useResume: false, CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
        orchestrator.IsExecuting.Should().BeFalse();
    }

    [Theory]
    [InlineData("Tests: 99999999999 passed")]
    [InlineData("Tests: 3 passed, 99999999999 failed")]
    [InlineData("✓ 99999999999 tests")]
    public void OutputParser_CountTooLargeForInt_IsIgnoredInsteadOfThrowing(string line)
    {
        var parser = new OutputParser();

        var act = () => parser.ProcessLine(line);

        act.Should().NotThrow();
        parser.TestResults.Should().BeNull();
    }
}
