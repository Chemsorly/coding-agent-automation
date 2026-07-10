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
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
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

        _mockProcess.Verify(p => p.StartAsync("test", "/tmp", false, It.IsAny<CancellationToken>(), "abc-123"), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithoutResumeSessionId_PassesNull()
    {
        await _orchestrator.ExecutePromptAsync("test", "/tmp", useResume: true, CancellationToken.None);

        _mockProcess.Verify(p => p.StartAsync("test", "/tmp", true, It.IsAny<CancellationToken>(), null), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_ResumeSessionId_IsForwardedCorrectly()
    {
        string? capturedSessionId = null;
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, string?>((_, _, _, _, sid) => capturedSessionId = sid)
            .ReturnsAsync(0);

        await _orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None, resumeSessionId: "session-xyz");

        capturedSessionId.Should().Be("session-xyz");
    }
}
