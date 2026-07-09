using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.UnitTests;

public class QualityGateValidatorProcessCancellationTests
{
    /// <summary>
    /// Verifies that external cancellation kills the child process tree and throws
    /// OperationCanceledException. The test captures the child PID via a temp file,
    /// triggers cancellation, then asserts the process no longer exists — proving Kill
    /// was actually called.
    /// </summary>
    [Fact(Timeout = 15000)]
    public async Task RunProcessAsync_ExternalCancellation_KillsProcessAndThrowsOperationCancelled()
    {
        var validator = new ExposedRunProcessValidator();
        using var cts = new CancellationTokenSource();
        var pidFile = Path.Combine(Path.GetTempPath(), $"qg_test_pid_{Guid.NewGuid():N}");

        try
        {
            // Start a process that writes its PID to a file, then sleeps
            var task = validator.RunProcessExposedAsync(
                "bash", $"-c \"echo $$ > {pidFile} && sleep 60\"",
                Directory.GetCurrentDirectory(),
                cts.Token,
                timeout: TimeSpan.FromMinutes(5));

            // Wait for the PID file to be written (process has started)
            int childPid = await WaitForPidFileAsync(pidFile, TimeSpan.FromSeconds(5));

            // Verify the process is actually running
            // TODO: This assertion is vacuous — GetProcessById never returns null (it throws
            // ArgumentException if not found). Replace with HasExited.Should().BeFalse() for a
            // meaningful check.
            Process.GetProcessById(childPid).Should().NotBeNull();

            // Cancel — this should trigger the kill path
            await cts.CancelAsync();

            // Assert OperationCanceledException is thrown
            var act = () => task;
            await act.Should().ThrowAsync<OperationCanceledException>();

            // Give the OS a moment to clean up
            await Task.Delay(200);

            // Assert the child process is actually dead
            // TODO: This check relies on Process.Dispose() (from the using block in RunProcessAsync)
            // having already reaped the zombie process. If .NET runtime disposal behavior changes or
            // OS scheduling delays reaping past 200ms under load, this could spuriously fail.
            // Consider polling with a short timeout or checking HasExited on a captured Process handle.
            var getProcess = () => Process.GetProcessById(childPid);
            getProcess.Should().Throw<ArgumentException>();
        }
        finally
        {
            try { File.Delete(pidFile); } catch { }
        }
    }

    /// <summary>
    /// Verifies that the timeout path still throws TimeoutException (regression check).
    /// </summary>
    // TODO: Add [Fact(Timeout = ...)] to convert a hang into a clear failure if TimeoutException is not thrown
    [Fact(Timeout = 15000)]
    public async Task RunProcessAsync_Timeout_ThrowsTimeoutException()
    {
        var validator = new ExposedRunProcessValidator();

        var act = () => validator.RunProcessExposedAsync(
            "sleep", "60",
            Directory.GetCurrentDirectory(),
            CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(500));

        await act.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>
    /// Verifies normal completion still returns exit code, stdout, and stderr.
    /// </summary>
    [Fact]
    public async Task RunProcessAsync_NormalCompletion_ReturnsExitCodeAndOutput()
    {
        var validator = new ExposedRunProcessValidator();

        var (exitCode, stdout, stderr) = await validator.RunProcessExposedAsync(
            "echo", "hello",
            Directory.GetCurrentDirectory(),
            CancellationToken.None,
            timeout: TimeSpan.FromSeconds(30));

        exitCode.Should().Be(0);
        stdout.Trim().Should().Be("hello");
        stderr.Should().BeEmpty();
    }

    private static async Task<int> WaitForPidFileAsync(string path, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (File.Exists(path))
            {
                var content = (await File.ReadAllTextAsync(path)).Trim();
                if (int.TryParse(content, out var pid))
                    return pid;
            }
            await Task.Delay(50);
        }
        throw new TimeoutException($"PID file was not written within {timeout.TotalSeconds}s");
    }

    /// <summary>
    /// Subclass that exposes the private protected RunProcessAsync for direct testing.
    /// </summary>
    private sealed class ExposedRunProcessValidator : QualityGateValidator
    {
        public ExposedRunProcessValidator() : base(Serilog.Log.Logger)
        {
        }

        public Task<(int ExitCode, string Stdout, string Stderr)> RunProcessExposedAsync(
            string fileName, string arguments, string workingDirectory,
            CancellationToken ct, TimeSpan timeout)
        {
            return RunProcessAsync(fileName, arguments, workingDirectory, ct, timeout);
        }
    }
}
