using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

public class KiroCliOrchestratorResumeIdTests
{
    private readonly Mock<IProcessWrapper> _mockProcess;
    private readonly KiroCliOrchestrator _orchestrator;

    public KiroCliOrchestratorResumeIdTests()
    {
        _mockProcess = new Mock<IProcessWrapper>();
        _mockProcess.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(0);

        var config = new global::KiroCliLib.Configuration.Configuration();
        var logger = new Mock<ILogger>().Object;
        _orchestrator = new KiroCliOrchestrator(
            config, logger,
            () => _mockProcess.Object);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithResumeSessionId_PassesToProcessWrapper()
    {
        await _orchestrator.ExecutePromptAsync("test", "/tmp", useResume: false, CancellationToken.None, resumeSessionId: "abc-123");

        _mockProcess.Verify(p => p.StartAsync(
            "test", "/tmp", false,
            It.IsAny<CancellationToken>(), "abc-123",
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithoutResumeSessionId_PassesNull()
    {
        await _orchestrator.ExecutePromptAsync("test", "/tmp", useResume: true, CancellationToken.None);

        _mockProcess.Verify(p => p.StartAsync(
            "test", "/tmp", true,
            It.IsAny<CancellationToken>(), null,
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_ResumeSessionId_IsForwardedCorrectly()
    {
        string? capturedSessionId = null;
        _mockProcess.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, bool, CancellationToken, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(0);

        await _orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None, resumeSessionId: "session-xyz");

        capturedSessionId.Should().Be("session-xyz");
    }

    // ─── Session that cannot be loaded (kiro-cli 2.29: exit 1, "error: ACP load_session failed") ───

    /// <summary>A process wrapper that prints <paramref name="stderr"/> and exits with <paramref name="exitCode"/>.</summary>
    private static Mock<IProcessWrapper> Process(int exitCode, string? stderr = null)
    {
        var process = new Mock<IProcessWrapper>();
        process.Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback(() =>
            {
                if (stderr is not null)
                    process.Raise(p => p.ErrorReceived += null, process.Object, stderr);
            })
            .ReturnsAsync(exitCode);
        return process;
    }

    private static KiroCliOrchestrator Orchestrator(params Mock<IProcessWrapper>[] processes)
    {
        var queue = new Queue<IProcessWrapper>(processes.Select(p => p.Object));
        return new KiroCliOrchestrator(
            new global::KiroCliLib.Configuration.Configuration(), new Mock<ILogger>().Object, () => queue.Dequeue());
    }

    [Fact]
    public async Task ExecutePromptAsync_ResumeIdThatCannotBeLoaded_RunsThePromptInAFreshSession()
    {
        var stale = Process(ExitCodes.GeneralFailure, "\u001b[31merror: ACP load_session failed\u001b[0m");
        var fresh = Process(ExitCodes.Success);
        var lines = new List<string>();

        var exitCode = await Orchestrator(stale, fresh).ExecutePromptAsync(
            "prompt", "/ws", useResume: true, CancellationToken.None,
            onOutputLine: line => { lines.Add(line); return Task.CompletedTask; },
            resumeSessionId: "stale-id");

        exitCode.Should().Be(ExitCodes.Success);
        fresh.Verify(p => p.StartAsync(
            "prompt", "/ws", false,
            It.IsAny<CancellationToken>(), null,
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
        lines.Should().ContainSingle().Which.Should().Contain("stale-id");
    }

    [Fact]
    public async Task ExecutePromptAsync_ResumeIdFailingForAnotherReason_IsNotRetried()
    {
        var failed = Process(ExitCodes.GeneralFailure, "error: model refused the request");
        var unused = Process(ExitCodes.Success);

        var exitCode = await Orchestrator(failed, unused).ExecutePromptAsync(
            "prompt", "/ws", useResume: true, CancellationToken.None, resumeSessionId: "abc-123");

        exitCode.Should().Be(ExitCodes.GeneralFailure);
        unused.Verify(p => p.StartAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Never);
    }

    [Fact]
    public async Task ExecutePromptAsync_LoadFailureWithoutAResumeId_IsNotRetried()
    {
        var failed = Process(ExitCodes.GeneralFailure, "error: ACP load_session failed");
        var unused = Process(ExitCodes.Success);

        var exitCode = await Orchestrator(failed, unused).ExecutePromptAsync(
            "prompt", "/ws", useResume: true, CancellationToken.None);

        exitCode.Should().Be(ExitCodes.GeneralFailure);
        unused.Verify(p => p.StartAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(),
            It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Never);
    }
}
