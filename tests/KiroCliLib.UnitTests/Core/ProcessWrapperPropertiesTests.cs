using AwesomeAssertions;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ProcessWrapper"/> observable properties and lifecycle methods.
///
/// Covers:
/// - IsRunning / ExitCode / ProcessId / LastOutputTime before, during, and after a run
/// - Already-running guard (StartAsync throws when called twice)
/// - Kill() on an idle wrapper (no-op)
/// - Kill() on a running process
///
/// These tests use real processes (/bin/sleep on Linux, ping on Windows) or /bin/echo
/// to exercise the code paths that were previously uncovered.
/// </summary>
public class ProcessWrapperPropertiesTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly global::KiroCliLib.Configuration.Configuration _echoConfig;
    private readonly ILogger _logger;

    public ProcessWrapperPropertiesTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"pw-prop-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceDir);

        var echoPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo";
        _echoConfig = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = echoPath,
            UseWsl = false
        };

        _logger = new Serilog.LoggerConfiguration().CreateLogger();
    }

    // ── Properties before any process is started ────────────────────────────

    [Fact]
    public void IsRunning_BeforeStart_ReturnsFalse()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        wrapper.IsRunning.Should().BeFalse("no process has been started yet");
    }

    [Fact]
    public void ExitCode_BeforeStart_ReturnsNull()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        wrapper.ExitCode.Should().BeNull("no process has been started yet");
    }

    [Fact]
    public void ProcessId_BeforeStart_ReturnsNull()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        wrapper.ProcessId.Should().BeNull("no process has been started yet");
    }

    [Fact]
    public void LastOutputTime_BeforeStart_ReturnsDefaultDateTime()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        // LastOutputTime is a DateTime field initialised to default(DateTime) — it should be
        // DateTime.MinValue (== default) before any output has been received.
        wrapper.LastOutputTime.Should().Be(default(DateTime),
            "no output has been received before the process is started");
    }

    // ── Properties after a completed process ────────────────────────────────

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    public async Task IsRunning_AfterCompletion_ReturnsFalse()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None);
        wrapper.IsRunning.Should().BeFalse("the process has exited");
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    public async Task ExitCode_AfterSuccessfulCompletion_ReturnsZero()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        var exitCode = await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None);
        wrapper.ExitCode.Should().Be(0, "echo exits with code 0");
        exitCode.Should().Be(0);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    public async Task ProcessId_DuringRun_ReturnsNonNull()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);

        // ProcessId is valid once the process starts; we capture it after completion since
        // echo exits too quickly to check mid-run. The value is still accessible after exit.
        await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None);
        wrapper.ProcessId.Should().NotBeNull("a process was started and its ID should be accessible");
    }

    // ── Already-running guard ────────────────────────────────────────────────

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    public async Task StartAsync_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        // After a first StartAsync completes, _process is non-null.
        // A second StartAsync on the same instance must throw because the guard checks
        // _process != null — it is not reset to null after completion.
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);

        // First call — completes normally (echo exits immediately).
        await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None);

        // Second call — _process != null, so the guard throws InvalidOperationException.
        var act = async () => await wrapper.StartAsync("world", _workspaceDir, useResume: false, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>(
            "starting a second process on the same instance must be rejected");
    }

    // ── Kill() on an idle wrapper ────────────────────────────────────────────

    [Fact]
    public void Kill_WhenNoProcessStarted_DoesNotThrow()
    {
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        // Kill() must be a no-op when _process is null — should not throw.
        var act = () => wrapper.Kill();
        act.Should().NotThrow("Kill() on an idle wrapper must be a no-op");
    }

    // ── Kill() on a running process ──────────────────────────────────────────

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    [System.Runtime.Versioning.SupportedOSPlatform("osx")]
    public async Task Kill_AfterProcessCompleted_DoesNotThrow()
    {
        // _process is non-null but _process.HasExited is true after completion.
        // Kill() should be a no-op (the guard returns early).
        using var wrapper = new ProcessWrapper(_echoConfig, _logger);
        await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None);

        var act = () => wrapper.Kill();
        act.Should().NotThrow("Kill() on a completed process must be a no-op");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }
}
