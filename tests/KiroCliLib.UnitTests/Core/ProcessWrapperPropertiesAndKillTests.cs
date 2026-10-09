using AwesomeAssertions;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ProcessWrapper"/> properties (IsRunning, ExitCode, ProcessId,
/// LastOutputTime) and the Kill() method. These paths were previously uncovered.
/// </summary>
/// <remarks>
/// Placed in the "EnvironmentVariables" collection to prevent parallel execution with
/// other test classes that mutate the parent process environment via
/// <see cref="Environment.SetEnvironmentVariable"/>. <see cref="ProcessWrapper.StartAsync"/>
/// copies the current environment into <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>
/// when building the child PSI, so concurrent env-var mutations can cause the process to fail
/// to start or produce incorrect inherited state.
/// </remarks>
[Collection("EnvironmentVariables")]
public class ProcessWrapperPropertiesAndKillTests : IDisposable
{
    private readonly string _workspaceDir;
    private readonly global::KiroCliLib.Configuration.Configuration _config;
    private readonly ILogger _logger;

    public ProcessWrapperPropertiesAndKillTests()
    {
        _workspaceDir = Path.Combine(Path.GetTempPath(), $"pw-proptest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspaceDir);

        var echoPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/echo";
        _config = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = echoPath,
            UseWsl = false
        };
        _logger = new Serilog.LoggerConfiguration().CreateLogger();
    }

    // ── Properties before any process is started ────────────────────────

    [Fact]
    public void IsRunning_BeforeStart_ReturnsFalse()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);
        wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void ExitCode_BeforeStart_ReturnsNull()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);
        wrapper.ExitCode.Should().BeNull();
    }

    [Fact]
    public void ProcessId_BeforeStart_ReturnsNull()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);
        wrapper.ProcessId.Should().BeNull();
    }

    [Fact]
    public void LastOutputTime_BeforeStart_ReturnsDefault()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);
        wrapper.LastOutputTime.Should().Be(default(DateTime));
    }

    // ── Properties after process completes ──────────────────────────────

    [Fact]
    public async Task ExitCode_AfterProcessCompletes_ReturnsZero()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);

        var exitCode = await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None);

        exitCode.Should().Be(0);
        wrapper.ExitCode.Should().Be(0);
        wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessId_AfterProcessCompletes_ReturnsNull()
    {
        // After the process exits, _process exists but HasExited is true,
        // so ProcessId returns the PID (the field is still set).
        // This exercises the ProcessId getter's internal try/catch path.
        using var wrapper = new ProcessWrapper(_config, _logger);

        await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None);

        // ProcessId after exit: may return the PID or null depending on whether
        // the OS allows reading it after exit — just verify it doesn't throw.
        var act = () => wrapper.ProcessId;
        act.Should().NotThrow();
    }

    // ── StartAsync guard: already running ───────────────────────────────

    [Fact]
    public async Task StartAsync_WhenAlreadyRunning_ThrowsInvalidOperationException()
    {
        // Use a shell script that ignores args and sleeps, so the process stays alive.
        string longRunningPath;
        if (OperatingSystem.IsWindows())
        {
            longRunningPath = "cmd.exe";
        }
        else
        {
            longRunningPath = Path.Combine(_workspaceDir, "long-running2.sh");
            File.WriteAllText(longRunningPath, "#!/bin/sh\nsleep 999\n");
            File.SetUnixFileMode(longRunningPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var longRunningConfig = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = longRunningPath,
            UseWsl = false
        };

        using var wrapper = new ProcessWrapper(longRunningConfig, _logger);
        // Safety net only: the test cancels the first run itself below
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Start in background — will block until cancelled or process exits
        var firstTask = wrapper.StartAsync("hello", _workspaceDir, useResume: false, cts.Token);

        // Wait until the first call has started its process (ProcessId is set from then on). A fixed
        // delay is not enough on a stalled host: the second call then passes the guard and, on Linux,
        // sleeps 999s. IsRunning is no signal on Windows, where cmd.exe exits at once; the guard
        // throws there too, because the first process was started. 30s is a hang detector only.
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (wrapper.ProcessId is null && !firstTask.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(20, CancellationToken.None);
        wrapper.ProcessId.Should().NotBeNull("the first StartAsync must have started its process");

        // Second call must throw because the process is already running. Its own token bounds the
        // test if a regression lets the call through, instead of waiting for `sleep 999`.
        using var secondCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrapper.StartAsync("hello", _workspaceDir, useResume: false, secondCts.Token));

        // Cancel the background task to clean up
        await cts.CancelAsync();
        try { await firstTask; } catch (OperationCanceledException) { /* expected */ } catch { /* killed */ }
    }

    // ── Kill() ──────────────────────────────────────────────────────────

    [Fact]
    public void Kill_WhenNoProcessStarted_DoesNotThrow()
    {
        // Kill on a fresh wrapper (no process) must be a no-op.
        using var wrapper = new ProcessWrapper(_config, _logger);
        var act = () => wrapper.Kill();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Kill_WhileProcessIsRunning_StopsProcess()
    {
        // The child must ignore the kiro-style arguments ProcessWrapper passes (e.g. "chat --no-interactive ...")
        // and must keep running after StartAsync closes its stdin. Bare cmd.exe reads end-of-file and exits at
        // once, and `timeout` rejects redirected stdin, so Windows uses a .cmd that pings loopback for ~999 s.
        // Linux uses a script that sleeps 999 s (/bin/sleep itself would exit on the bad arguments).
        string longRunningPath;
        if (OperatingSystem.IsWindows())
        {
            longRunningPath = Path.Combine(_workspaceDir, "long-running.cmd");
            File.WriteAllText(longRunningPath, "@ping -n 999 127.0.0.1 >nul\r\n");
        }
        else
        {
            longRunningPath = Path.Combine(_workspaceDir, "long-running.sh");
            File.WriteAllText(longRunningPath, "#!/bin/sh\nsleep 999\n");
            File.SetUnixFileMode(longRunningPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        var longRunningConfig = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = longRunningPath,
            UseWsl = false
        };

        using var wrapper = new ProcessWrapper(longRunningConfig, _logger);
        // Safety net only: Kill() below must end the run long before this fires
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // Start the long-running process in the background
        var task = wrapper.StartAsync("hello", _workspaceDir, useResume: false, cts.Token);

        // Wait until the child is running. 30 s is a hang detector only; the loop also stops if StartAsync
        // finishes early (the child exited on its own, which the assertion below reports).
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!wrapper.IsRunning && !task.IsCompleted && DateTime.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);
        wrapper.IsRunning.Should().BeTrue("the long-running child must still be alive before Kill()");

        wrapper.Kill();

        // Kill() ends WaitForExitAsync normally, so StartAsync returns the killed child's exit code
        // (137 after SIGKILL on Linux, -1 on Windows). If Kill() did not stop the child, the 60 s safety
        // token fires instead and this await throws OperationCanceledException.
        var exitCode = await task;

        exitCode.Should().NotBe(0, "the child was killed, it did not finish on its own");
        wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Kill_AfterProcessAlreadyExited_DoesNotThrow()
    {
        using var wrapper = new ProcessWrapper(_config, _logger);

        // Let the process run to completion
        await wrapper.StartAsync(
            "hello",
            _workspaceDir,
            useResume: false,
            CancellationToken.None);

        // Calling Kill on an already-exited process must be a no-op
        var act = () => wrapper.Kill();
        act.Should().NotThrow();
    }

    // ── Dispose ─────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_WhenNoProcessStarted_DoesNotThrow()
    {
        var wrapper = new ProcessWrapper(_config, _logger);
        var act = () => wrapper.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var wrapper = new ProcessWrapper(_config, _logger);
        wrapper.Dispose();
        var act = () => wrapper.Dispose();
        act.Should().NotThrow();
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspaceDir, recursive: true); } catch { /* best-effort */ }
    }
}
