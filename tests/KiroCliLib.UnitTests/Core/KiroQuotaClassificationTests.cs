using AwesomeAssertions;
using KiroCliLib.Core;
using Moq;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Spec 056 req 5.2, 5.5 (task 3.1): only kiro-cli's own <c>error:</c> lines of a failed call say the quota is used up
/// or the request was throttled; the model's text and tool output never count.
/// </summary>
public class KiroQuotaClassificationTests
{
    // Observed on widow 2026-11-02 (the request id is shortened).
    private const string WidowLine =
        "Error: Internal error (code -32603): Encountered an error in the response stream: The monthly usage limit has been reached (request_id: 0f3c)";

    [Theory]
    [InlineData(WidowLine)]
    [InlineData("error: You've reached your monthly usage limit")]
    [InlineData("ERROR: You've reached your overage limit.")]
    [InlineData("   error: You've reached your monthly usage limit")]
    public void Classify_QuotaLines_AreQuota(string line) =>
        KiroErrorText.Classify(line).Should().Be(KiroErrorKind.QuotaExhausted);

    [Theory]
    [InlineData("error: The request was throttled")]
    [InlineData("Error: Too many requests, please wait")]
    public void Classify_ThrottlingLines_AreRateLimited(string line) =>
        KiroErrorText.Classify(line).Should().Be(KiroErrorKind.RateLimited);

    [Theory]
    [InlineData("error: ACP load_session failed")]
    [InlineData("The monthly usage limit has been reached, says the issue I am fixing")]
    [InlineData("▶ grep 'monthly usage limit' docs/spec.md")]
    [InlineData("")]
    [InlineData(null)]
    public void Classify_OtherLines_AreNothing(string? line) =>
        KiroErrorText.Classify(line).Should().Be(KiroErrorKind.None);

    private static Mock<IProcessWrapper> Process(int exitCode, string? stdout = null, string? stderr = null)
    {
        var process = new Mock<IProcessWrapper>();
        process.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback(() =>
            {
                if (stdout is not null)
                    process.Raise(p => p.OutputReceived += null, process.Object, stdout);
                if (stderr is not null)
                    process.Raise(p => p.ErrorReceived += null, process.Object, stderr);
            })
            .ReturnsAsync(exitCode);
        return process;
    }

    private static async Task<KiroCliOrchestrator> RunAsync(Mock<IProcessWrapper> process)
    {
        var orchestrator = new KiroCliOrchestrator(
            new global::KiroCliLib.Configuration.Configuration(), new Mock<ILogger>().Object, () => process.Object);
        await orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None);
        return orchestrator;
    }

    [Fact]
    public async Task FailedCall_WithTheWidowLineOnStderr_RecordsTheQuotaLine()
    {
        var orchestrator = await RunAsync(Process(ExitCodes.GeneralFailure, stderr: "\u001b[31m" + WidowLine + "\u001b[0m"));

        orchestrator.LastRunQuotaExhausted.Should().Be(WidowLine);
        orchestrator.LastRunRateLimited.Should().BeFalse();
    }

    [Fact]
    public async Task FailedCall_WithTheQuotaLineOnStdout_RecordsIt()
    {
        var orchestrator = await RunAsync(Process(ExitCodes.GeneralFailure, stdout: "error: You've reached your overage limit."));

        orchestrator.LastRunQuotaExhausted.Should().Be("error: You've reached your overage limit.");
    }

    [Fact]
    public async Task FailedCall_Throttled_IsRateLimitedNotQuota()
    {
        var orchestrator = await RunAsync(Process(ExitCodes.GeneralFailure, stderr: "error: The request was throttled"));

        orchestrator.LastRunQuotaExhausted.Should().BeNull();
        orchestrator.LastRunRateLimited.Should().BeTrue();
    }

    // req 5.5: tool output with the phrase, in a run that exits 0, records nothing.
    [Fact]
    public async Task SuccessfulCall_WithAQuotaErrorLine_RecordsNothing()
    {
        var orchestrator = await RunAsync(Process(ExitCodes.Success, stdout: "error: The monthly usage limit has been reached"));

        orchestrator.LastRunQuotaExhausted.Should().BeNull();
        orchestrator.LastRunRateLimited.Should().BeFalse();
    }

    // req 5.5: tool output with the phrase, in a run that fails for another reason, records nothing.
    [Fact]
    public async Task FailedCall_WithThePhraseOnlyInToolOutput_RecordsNothing()
    {
        var orchestrator = await RunAsync(Process(ExitCodes.GeneralFailure,
            stdout: "> cat requirements.md: The monthly usage limit has been reached",
            stderr: "error: process exited unexpectedly"));

        orchestrator.LastRunQuotaExhausted.Should().BeNull();
    }

    [Fact]
    public async Task NextCall_ClearsTheLastQuotaLine()
    {
        var first = Process(ExitCodes.GeneralFailure, stderr: WidowLine);
        var second = Process(ExitCodes.Success);
        var queue = new Queue<IProcessWrapper>([first.Object, second.Object]);
        var orchestrator = new KiroCliOrchestrator(
            new global::KiroCliLib.Configuration.Configuration(), new Mock<ILogger>().Object, () => queue.Dequeue());

        await orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None);
        await orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None);

        orchestrator.LastRunQuotaExhausted.Should().BeNull();
    }
}
