using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests that environment variables passed to <see cref="IKiroCliOrchestrator.ExecutePromptAsync"/>
/// are forwarded all the way to <see cref="IProcessWrapper.StartAsync"/>. Verifies the full chain:
/// KiroCliOrchestrator → ProcessWrapper (no global env var mutation).
/// </summary>
public class KiroCliOrchestratorEnvironmentVariablesTests
{
    private readonly Mock<IProcessWrapper> _mockProcess;
    private readonly KiroCliOrchestrator _orchestrator;

    public KiroCliOrchestratorEnvironmentVariablesTests()
    {
        _mockProcess = new Mock<IProcessWrapper>();
        _mockProcess
            .Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .ReturnsAsync(0);

        var config = new global::KiroCliLib.Configuration.Configuration();
        var logger = new Mock<ILogger>().Object;
        _orchestrator = new KiroCliOrchestrator(config, logger, () => _mockProcess.Object);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithEnvironmentVariables_ForwardsToProcessWrapper()
    {
        var envVars = new Dictionary<string, string>
        {
            ["MY_SECRET"] = "secret-value-xyz",
            ["OTHER_KEY"] = "other-value-abc"
        };

        await _orchestrator.ExecutePromptAsync(
            "test prompt", "/tmp/ws", useResume: false,
            CancellationToken.None,
            environmentVariables: envVars);

        _mockProcess.Verify(p => p.StartAsync(
            "test prompt", "/tmp/ws", false,
            It.IsAny<CancellationToken>(),
            null,
            envVars),
            Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_WithNullEnvironmentVariables_ForwardsNullToProcessWrapper()
    {
        await _orchestrator.ExecutePromptAsync(
            "test prompt", "/tmp/ws", useResume: false,
            CancellationToken.None);

        _mockProcess.Verify(p => p.StartAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<string?>(),
            null),
            Times.Once);
    }

    [Fact]
    public async Task ExecutePromptAsync_EnvironmentVariables_CapturedCorrectly()
    {
        IReadOnlyDictionary<string, string>? capturedEnv = null;

        _mockProcess
            .Setup(p => p.StartAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<string?>(),
                It.IsAny<IReadOnlyDictionary<string, string>?>()))
            .Callback<string, string, bool, CancellationToken, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, _, env) => capturedEnv = env)
            .ReturnsAsync(0);

        var expected = new Dictionary<string, string> { ["API_KEY"] = "test-api-key-1234" };

        await _orchestrator.ExecutePromptAsync(
            "prompt", "/workspace", useResume: true,
            CancellationToken.None,
            environmentVariables: expected);

        capturedEnv.Should().NotBeNull();
        capturedEnv.Should().ContainKey("API_KEY").WhoseValue.Should().Be("test-api-key-1234");
    }

    [Fact]
    public async Task ExecutePromptAsync_EnvironmentVariables_DoNotPollutateParentProcess()
    {
        // This test verifies the key invariant: passing env vars via the new parameter
        // does NOT call Environment.SetEnvironmentVariable on the parent process.
        var sentinelKey = $"KIRO_TEST_{Guid.NewGuid():N}";
        var envVars = new Dictionary<string, string> { [sentinelKey] = "should-not-appear-globally" };

        try
        {
            await _orchestrator.ExecutePromptAsync(
                "prompt", "/ws", useResume: false,
                CancellationToken.None,
                environmentVariables: envVars);

            // The sentinel key must NOT be set in the parent process environment
            Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable(sentinelKey, null);
        }
    }
}
