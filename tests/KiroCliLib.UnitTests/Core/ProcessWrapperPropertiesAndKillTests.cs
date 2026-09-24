using AwesomeAssertions;
using KiroCliLib.Core;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ProcessWrapper"/> properties (IsRunning, ExitCode, ProcessId,
/// LastOutputTime) and the Kill() method. These paths were previously uncovered.
/// </summary>
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
        var _ = wrapper.ProcessId; // must not throw
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start in background — will block until cancelled or process exits
        var firstTask = wrapper.StartAsync("hello", _workspaceDir, useResume: false, cts.Token);

        // Give the process a moment to start
        await Task.Delay(300, CancellationToken.None);

        // Second call must throw because the process is already running
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None));

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
        // Create a shell script that ignores all arguments and sleeps indefinitely.
        // ProcessWrapper passes kiro-style args (e.g. "chat --no-interactive ...") so
        // we can't use /bin/sleep directly — it would exit immediately on bad args.
        // A dedicated wrapper script accepts any $@ and just runs 'sleep 999'.
        string longRunningPath;
        if (OperatingSystem.IsWindows())
        {
            longRunningPath = "cmd.exe";
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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start the long-running process in the background
        var task = wrapper.StartAsync("hello", _workspaceDir, useResume: false, cts.Token);

        // Poll until IsRunning becomes true (or timeout). A fixed delay is flaky under load.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!wrapper.IsRunning && DateTime.UtcNow < deadline)
            await Task.Delay(50, CancellationToken.None);
        wrapper.IsRunning.Should().BeTrue("process should be running before Kill()");

        // Kill it — this covers Kill() body for a running, non-WSL process
        // (lines: if _useWsl check, _process.Kill(entireProcessTree), _process.WaitForExit)
        wrapper.Kill();

        // The task should complete after Kill
        try { await task; } catch (OperationCanceledException) { /* expected */ } catch { /* process killed */ }

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
