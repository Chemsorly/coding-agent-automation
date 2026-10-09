using System.Collections.Concurrent;
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
[Collection("EnvironmentVariables")]
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

    [Fact]
    public async Task StartAsync_StripsOtelKeysInheritedFromParentProcess()
    {
        // Arrange: one parent variable of each kind StripTelemetry removes, plus a control variable that
        // must reach the child (it proves the child really printed its inherited environment).
        var controlKey = $"KIRO_PW_CONTROL_{Guid.NewGuid():N}";
        string[] strippedKeys = ["OTEL_SERVICE_NAME", "OTEL_EXPORTER_OTLP_HEADERS", "TRACEPARENT", "AGENT_CLAUDE_API_KEY"];
        var parentValues = new Dictionary<string, string>
        {
            ["OTEL_SERVICE_NAME"] = "coding-agent-worker-test",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "pw-test-headers",
            ["TRACEPARENT"] = "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01",
            ["AGENT_CLAUDE_API_KEY"] = "pw-test-api-key",
            [controlKey] = "keep-me"
        };
        var previous = parentValues.Keys.ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k));
        foreach (var (key, value) in parentValues)
            Environment.SetEnvironmentVariable(key, value);
        try
        {
            var (exitCode, childEnvironment) = await RunEnvironmentDumpAsync(environmentVariables: null);

            exitCode.Should().Be(0);
            childEnvironment.Should().Contain($"{controlKey}=keep-me",
                "the child must print the environment it inherited, otherwise the absence checks prove nothing");
            foreach (var strippedKey in strippedKeys)
            {
                childEnvironment.Should().NotContain(
                    line => line.StartsWith(strippedKey + "=", StringComparison.OrdinalIgnoreCase),
                    $"ProcessWrapper.StartAsync must strip {strippedKey} from the kiro-cli child environment");
            }

            // Parent environment is intact: StripTelemetry only touches the ProcessStartInfo copy
            Environment.GetEnvironmentVariable("OTEL_SERVICE_NAME").Should().Be("coding-agent-worker-test",
                "StripTelemetry must not mutate the parent process environment");
        }
        finally
        {
            foreach (var (key, value) in previous)
                Environment.SetEnvironmentVariable(key, value);
        }
    }

    [Fact]
    public async Task StartAsync_PreservesInjectedEnvironmentVariablesAfterOtelStrip()
    {
        // Arrange — set OTEL key in parent so it is inherited into the PSI copy
        const string otelKey = "OTEL_SERVICE_NAME";
        var previous = Environment.GetEnvironmentVariable(otelKey);
        Environment.SetEnvironmentVariable(otelKey, "coding-agent-worker-test");
        try
        {
            // Injected non-OTEL key — must survive the strip and reach the child
            var injectedKey = $"MY_SECRET_{Guid.NewGuid():N}";
            const string injectedValue = "keep-me";

            var envVars = new Dictionary<string, string> { [injectedKey] = injectedValue };

            var (exitCode, childEnvironment) = await RunEnvironmentDumpAsync(envVars);

            exitCode.Should().Be(0);
            childEnvironment.Should().Contain($"{injectedKey}={injectedValue}",
                "per-invocation variables are injected after the strip and must reach the child");
            childEnvironment.Should().NotContain(
                line => line.StartsWith(otelKey + "=", StringComparison.OrdinalIgnoreCase),
                "the inherited OTEL key must still be stripped when per-invocation variables are passed");

            // Injected vars must be child-scoped only — must not leak into parent process
            Environment.GetEnvironmentVariable(injectedKey).Should().BeNull(
                "injected vars must be child-scoped only");

            // The parent OTEL key is intact (StripTelemetry touches only the PSI copy)
            Environment.GetEnvironmentVariable(otelKey).Should().Be("coding-agent-worker-test");
        }
        finally
        {
            Environment.SetEnvironmentVariable(otelKey, previous);
        }
    }

    /// <summary>
    /// Returns a configuration whose KiroCliPath is a script that ignores its arguments and prints the
    /// child's environment as NAME=value lines, so a test can see what the child process inherited.
    /// </summary>
    private global::KiroCliLib.Configuration.Configuration CreateEnvironmentDumpConfig()
    {
        string scriptPath;
        if (OperatingSystem.IsWindows())
        {
            // A .cmd started with UseShellExecute=false runs under cmd.exe /c; `set` prints NAME=value lines.
            scriptPath = Path.Combine(_workspaceDir, "print-env.cmd");
            File.WriteAllText(scriptPath, "@set\r\n");
        }
        else
        {
            scriptPath = Path.Combine(_workspaceDir, "print-env.sh");
            File.WriteAllText(scriptPath, "#!/bin/sh\nenv\n");
            File.SetUnixFileMode(scriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = scriptPath,
            UseWsl = false
        };
    }

    /// <summary>
    /// Runs the environment-dump script via <see cref="ProcessWrapper"/> and returns the exit code
    /// and the child's stdout lines. <see cref="ProcessWrapper.StartAsync"/> awaits
    /// <see cref="System.Diagnostics.Process.WaitForExitAsync"/> and then delays 100 ms after
    /// cancelling output reads, so all lines are guaranteed to have been raised through
    /// <see cref="ProcessWrapper.OutputReceived"/> by the time this method returns.
    /// </summary>
    private async Task<(int ExitCode, IReadOnlyList<string> ChildEnvironment)> RunEnvironmentDumpAsync(
        IReadOnlyDictionary<string, string>? environmentVariables)
    {
        var lines = new ConcurrentQueue<string>();
        using var wrapper = new ProcessWrapper(CreateEnvironmentDumpConfig(), _logger);
        wrapper.OutputReceived += (_, line) => lines.Enqueue(line);

        var exitCode = await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None,
            resumeSessionId: null,
            environmentVariables: environmentVariables);

        return (exitCode, lines.ToList());
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }
}
