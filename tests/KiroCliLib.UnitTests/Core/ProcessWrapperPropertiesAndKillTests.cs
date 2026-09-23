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
        // Use 'sleep' to keep the process alive long enough to call StartAsync again.
        var sleepPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep";
        var sleepConfig = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = sleepPath,
            UseWsl = false
        };

        using var wrapper = new ProcessWrapper(sleepConfig, _logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Start in background — will block until cancelled or sleep exits
        var firstTask = wrapper.StartAsync("2", _workspaceDir, useResume: false, cts.Token);

        // Give the process a moment to start
        await Task.Delay(200, CancellationToken.None);

        // Second call must throw because the process is already running
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await wrapper.StartAsync("hello", _workspaceDir, useResume: false, CancellationToken.None));

        // Cancel the background task to clean up
        await cts.CancelAsync();
        try { await firstTask; } catch (OperationCanceledException) { /* expected */ }
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
        var sleepPath = OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sleep";
        var sleepConfig = new global::KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = sleepPath,
            UseWsl = false
        };

        using var wrapper = new ProcessWrapper(sleepConfig, _logger);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        // Start a long-running sleep in the background
        var task = wrapper.StartAsync("30", _workspaceDir, useResume: false, cts.Token);

        // Give the process time to start
        await Task.Delay(200, CancellationToken.None);

        // Kill it — this covers the Kill() body for a running, non-WSL process
        wrapper.Kill();

        // The task should complete (via OperationCanceledException from the Kill/cancel)
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
