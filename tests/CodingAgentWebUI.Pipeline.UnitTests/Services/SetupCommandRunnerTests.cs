using System.Runtime.InteropServices;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Unit tests for <see cref="SetupCommandRunner"/>.
/// Uses platform-aware shell commands so the suite runs on both Windows (cmd.exe) and Linux (/bin/bash).
/// </summary>
[Trait("Category", "Integration")]
public class SetupCommandRunnerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly List<string> _emittedLines = [];

    public SetupCommandRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"setup-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    // ── Cross-platform command helpers ──────────────────────────────────

    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>Echoes a string to stdout. Works on both cmd.exe and bash.</summary>
    private static string EchoStdout(string text) => $"echo {text}";

    /// <summary>Echoes a string to stderr and exits with the given code.</summary>
    private static string EchoStderrAndExit(string text, int exitCode) =>
        IsWindows
            ? $"echo {text} 1>&2 & exit {exitCode}"
            : $"echo '{text}' >&2; exit {exitCode}";

    /// <summary>Echoes an environment variable value to stdout.</summary>
    private static string EchoEnvVar(string varName) =>
        IsWindows ? $"echo %{varName}%" : $"echo ${varName}";

    /// <summary>Echoes a string to stdout then sleeps for a long time (for timeout tests).</summary>
    private static string EchoThenSleep(string text) =>
        IsWindows
            ? $"echo {text} & ping -n 9999 127.0.0.1 > nul"
            : $"echo '{text}'; sleep 9999";

    /// <summary>Sleeps for a long time (for timeout/cancellation tests).</summary>
    private static string SleepForever() =>
        IsWindows ? "ping -n 9999 127.0.0.1 > nul" : "sleep 9999";

    // ── Tests ────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsSuccessAndEmitsOutput()
    {
        var result = await SetupCommandRunner.RunAsync(
            EchoStdout("hello"), "Test Step", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeTrue();
        result.FailureMessage.Should().BeNull();
        result.Exception.Should().BeNull();
        _emittedLines.Should().Contain(line => line.Contains("hello"));
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_ReturnsFailureWithExitCodeAndStderr()
    {
        var result = await SetupCommandRunner.RunAsync(
            EchoStderrAndExit("some error", 5), "Auth check", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Auth check");
        result.FailureMessage.Should().Contain("5");
        result.FailureMessage.Should().Contain("some error");
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_TruncatesStderrTo500Chars()
    {
        var longError = new string('x', 600);

        var result = await SetupCommandRunner.RunAsync(
            EchoStderrAndExit(longError, 1), "Long Error Step", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Long Error Step");
        result.FailureMessage.Should().Contain("exit code 1");
        result.FailureMessage.Should().NotContain(longError);
    }

    [Fact]
    public async Task RunAsync_SecretsInjectedIntoProcessEnvironment()
    {
        var secrets = new Dictionary<string, string> { ["MY_SECRET_KEY"] = "secret-value-1234" };

        var result = await SetupCommandRunner.RunAsync(
            EchoEnvVar("MY_SECRET_KEY"), "Secret Test", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeTrue();
        // The secret value should be masked in emitted output
        _emittedLines.Should().NotContain(line => line.Contains("secret-value-1234"));
        _emittedLines.Should().Contain(line => line.Contains("***"));
    }

    [Fact]
    public async Task RunAsync_SecretValuesMaskedInOutput()
    {
        var secrets = new Dictionary<string, string> { ["TOKEN"] = "my-secret-token" };

        var result = await SetupCommandRunner.RunAsync(
            EchoStdout("my-secret-token"), "Mask Test", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeTrue();
        _emittedLines.Should().NotContain(line => line.Contains("my-secret-token"));
        _emittedLines.Should().Contain(line => line.Contains("***"));
    }

    [Fact]
    public async Task RunAsync_SecretValuesMaskedInFailureMessage()
    {
        var secrets = new Dictionary<string, string> { ["API_KEY"] = "super-secret-key" };

        var result = await SetupCommandRunner.RunAsync(
            EchoStderrAndExit("super-secret-key is invalid", 1), "Secret Failure", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().NotContain("super-secret-key");
        result.FailureMessage.Should().Contain("***");
    }

    [Fact]
    public async Task RunAsync_CancellationTokenRespected_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => SetupCommandRunner.RunAsync(
            SleepForever(), "Cancelled Step", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_ExceptionDuringProcessStart_ReturnsFailureWithException()
    {
        var invalidDir = Path.Combine(_tempDir, "nonexistent-" + Guid.NewGuid().ToString("N"));

        var result = await SetupCommandRunner.RunAsync(
            EchoStdout("hello"), "Bad Dir Step", invalidDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Bad Dir Step");
        result.FailureMessage.Should().Contain("threw an exception");
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_EmptySecrets_StillRunsSuccessfully()
    {
        var result = await SetupCommandRunner.RunAsync(
            EchoStdout("works"), "Empty Secrets", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeTrue();
        _emittedLines.Should().Contain(line => line.Contains("works"));
    }

    [Fact]
    public async Task RunAsync_StderrOutput_IsEmitted()
    {
        // Command exits 0 but writes to stderr
        var cmd = IsWindows
            ? "echo stderr output 1>&2"
            : "echo 'stderr output' >&2";

        var result = await SetupCommandRunner.RunAsync(
            cmd, "Stderr Step", _tempDir, new Dictionary<string, string>(),
            line => _emittedLines.Add(line), CancellationToken.None);

        result.Success.Should().BeTrue();
        _emittedLines.Should().Contain(line => line.Contains("stderr output"));
    }

    [Fact]
    public async Task RunAsync_NullEmitOutput_ThrowsArgumentNullException()
    {
        var act = () => SetupCommandRunner.RunAsync(
            EchoStdout("hello"), "Null Test", _tempDir, new Dictionary<string, string>(),
            null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsFailureWithTimeoutMessage()
    {
        var result = await SetupCommandRunner.RunAsync(
            SleepForever(), "Slow Step", _tempDir, new Dictionary<string, string>(),
            _ => { }, timeout: TimeSpan.FromSeconds(2), ct: CancellationToken.None);

        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("timed out");
        result.FailureMessage.Should().Contain("Slow Step");
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_Timeout_DoesNotProduceUnobservedTaskException()
    {
        var unobservedExceptions = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobservedExceptions.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var result = await SetupCommandRunner.RunAsync(
                EchoThenSleep("stdout data"), "Hang Step", _tempDir,
                new Dictionary<string, string>(),
                _ => { }, timeout: TimeSpan.FromSeconds(2), ct: CancellationToken.None);

            result.Success.Should().BeFalse();

            // Wait for pipe-read tasks to fault after process kill
            await Task.Delay(TimeSpan.FromSeconds(2));

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(100);

            unobservedExceptions.Should().BeEmpty(
                "Abandoned stdout/stderr tasks should have their exceptions observed by ContinueWith");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }

    [Fact]
    public void SetupCommandRunner_UsesCorrectShellForPlatform()
    {
        if (IsWindows)
        {
            SetupCommandRunner.ShellExecutable.Should().Be("cmd.exe");
            SetupCommandRunner.ShellFlag.Should().Be("/c");
        }
        else
        {
            SetupCommandRunner.ShellExecutable.Should().Be("/bin/bash");
            SetupCommandRunner.ShellFlag.Should().Be("-c");
        }
    }
}
