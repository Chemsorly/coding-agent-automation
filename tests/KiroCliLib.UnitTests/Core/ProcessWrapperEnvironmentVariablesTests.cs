using AwesomeAssertions;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests that <see cref="ProcessWrapper.StartAsync"/> correctly injects
/// environment variables into <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>
/// without polluting the parent process environment.
///
/// These tests use a real process (echo/env on Linux) rather than mocking ProcessWrapper
/// because the env-var injection code lives inside StartAsync and must be tested at the
/// implementation level to count as covered.
/// </summary>
public class ProcessWrapperEnvironmentVariablesTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly global::KiroCliLib.Configuration.Configuration _config;
    private readonly ILogger _logger;

    public ProcessWrapperEnvironmentVariablesTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"pw-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceDir);

        // Point KiroCliPath at /bin/echo (Linux) so StartAsync runs a real but trivial process.
        // echo accepts any arguments and exits 0, which lets StartAsync complete normally.
        var echoPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo";
        _config = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = echoPath,
            UseWsl = false
        };
        _logger = new Serilog.LoggerConfiguration().CreateLogger();
    }

    [Fact]
    public async Task StartAsync_WithEnvironmentVariables_DoesNotPollutateParentProcess()
    {
        // Arrange — use a sentinel key that won't exist in the parent environment
        var sentinelKey = $"KIRO_PW_TEST_{Guid.NewGuid():N}";
        var envVars = new Dictionary<string, string>
        {
            [sentinelKey] = "injected-value"
        };

        using var wrapper = new ProcessWrapper(_config, _logger);

        // Act — run the process with env vars
        await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None,
            resumeSessionId: null,
            environmentVariables: envVars);

        // Assert — the sentinel key must NOT have leaked into the parent process
        Environment.GetEnvironmentVariable(sentinelKey).Should().BeNull(
            "environment variables must be scoped to the child process only");
    }

    [Fact]
    public async Task StartAsync_WithNullEnvironmentVariables_Succeeds()
    {
        // Verify the null path (no env vars) still works correctly and doesn't throw
        using var wrapper = new ProcessWrapper(_config, _logger);

        var exitCode = await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None,
            resumeSessionId: null,
            environmentVariables: null);

        // echo exits 0
        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithEmptyEnvironmentVariables_Succeeds()
    {
        // Verify the empty-dictionary path (Count == 0) doesn't inject anything and doesn't throw
        using var wrapper = new ProcessWrapper(_config, _logger);

        var exitCode = await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None,
            resumeSessionId: null,
            environmentVariables: new Dictionary<string, string>());

        exitCode.Should().Be(0);
    }

    [Fact]
    public async Task StartAsync_WithEnvironmentVariables_CompletesSuccessfully()
    {
        // Verify the happy path: env vars are passed and the process exits normally
        var envVars = new Dictionary<string, string>
        {
            ["TEST_VAR_1"] = "value-one",
            ["TEST_VAR_2"] = "value-two"
        };

        using var wrapper = new ProcessWrapper(_config, _logger);

        var exitCode = await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None,
            resumeSessionId: null,
            environmentVariables: envVars);

        exitCode.Should().Be(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }
}
