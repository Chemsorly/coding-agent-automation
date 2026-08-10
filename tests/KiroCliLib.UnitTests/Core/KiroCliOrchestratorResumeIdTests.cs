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
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
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

        _mockProcess.Verify(p => p.StartAsync("test", "/tmp", false, It.IsAny<CancellationToken>(), "abc-123", It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithoutResumeSessionId_PassesNull()
    {
        await _orchestrator.ExecutePromptAsync("test", "/tmp", useResume: true, CancellationToken.None);

        _mockProcess.Verify(p => p.StartAsync("test", "/tmp", true, It.IsAny<CancellationToken>(), null, It.IsAny<IReadOnlyDictionary<string, string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_ResumeSessionId_IsForwardedCorrectly()
    {
        string? capturedSessionId = null;
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, bool, CancellationToken, string?, IReadOnlyDictionary<string, string>?>((_, _, _, _, sid, _) => capturedSessionId = sid)
            .ReturnsAsync(0);

        await _orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None, resumeSessionId: "session-xyz");

        capturedSessionId.Should().Be("session-xyz");
    }

    [Fact]
    public async Task ExecutePromptAsync_WithAdditionalEnv_PassesToProcessWrapper()
    {
        // Arrange
        var additionalEnv = new Dictionary<string, string>
        {
            ["MY_SECRET"] = "secret-value",
            ["ANOTHER_VAR"] = "another-value"
        };

        IReadOnlyDictionary<string, string>? capturedEnv = null;
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, bool, CancellationToken, string?, IReadOnlyDictionary<string, string>?>((_, _, _, _, _, env) => capturedEnv = env)
            .ReturnsAsync(0);

        // Act
        await _orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None, additionalEnv: additionalEnv);

        // Assert — additionalEnv must be threaded through to processWrapper.StartAsync
        capturedEnv.Should().NotBeNull("additionalEnv must be forwarded to the process wrapper");
        capturedEnv!["MY_SECRET"].Should().Be("secret-value");
        capturedEnv["ANOTHER_VAR"].Should().Be("another-value");
    }

    [Fact]
    public async Task ExecutePromptAsync_NullAdditionalEnv_PassesNullToProcessWrapper()
    {
        IReadOnlyDictionary<string, string>? capturedEnv = new Dictionary<string, string> { ["SENTINEL"] = "x" };
        _mockProcess.Setup(p => p.StartAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<CancellationToken>(), It.IsAny<string?>(), It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, bool, CancellationToken, string?, IReadOnlyDictionary<string, string>?>((_, _, _, _, _, env) => capturedEnv = env)
            .ReturnsAsync(0);

        await _orchestrator.ExecutePromptAsync("prompt", "/ws", useResume: false, CancellationToken.None);

        capturedEnv.Should().BeNull("null additionalEnv must be passed through as null");
    }
}
