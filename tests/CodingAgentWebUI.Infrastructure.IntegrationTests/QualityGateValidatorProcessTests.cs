using AwesomeAssertions;
using System.Runtime.InteropServices;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="QualityGateValidator"/> that spawn real OS processes.
/// Moved from QualityGateValidatorTests (unit tests) because real process spawning (~0.5–20s per test)
/// belongs in the integration test project where it can be filtered out of fast unit test runs.
/// </summary>
/// <remarks>
/// These tests require OS process execution (cmd.exe, ping, sleep, bash) and are excluded from
/// normal dotnet test runs by selecting tests/CodingAgentWebUI.Infrastructure.UnitTests/ only.
/// </remarks>
public sealed class QualityGateValidatorProcessTests
{
    // ── Cross-platform cancellation ─────────────────────────────────────

    [Fact]
    public async Task RunProcessAsync_ExternalCancellation_KillsProcessAndThrowsOperationCanceledException()
    {
        // Arrange: create a validator that exposes the real RunProcessAsync
        var validator = new ProcessExposingValidator();
        using var cts = new CancellationTokenSource();

        // Act: spawn a long-running process and cancel after a brief delay.
        // Use a cross-platform "sleep" equivalent:
        //   Linux: sleep 300
        //   Windows: cmd.exe /c "ping -n 301 127.0.0.1 > nul"  (each ping takes ~1s)
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        Func<Task> act;
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            act = () => validator.RunProcessPublicAsync(
                "cmd.exe", "/c ping -n 301 127.0.0.1", Directory.GetCurrentDirectory(), cts.Token, TimeSpan.FromMinutes(10));
        }
        else
        {
            act = () => validator.RunProcessPublicAsync(
                "sleep", "300", Directory.GetCurrentDirectory(), cts.Token, TimeSpan.FromMinutes(10));
        }

        // Assert: OperationCanceledException is thrown within bounded time.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await act.Should().ThrowAsync<OperationCanceledException>();
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));
    }

    // ── Linux pipe-drain tests (skipped on Windows) ─────────────────────

    // TODO: BothPipesComplete exercises only the happy path where both pipes close instantly.
    // It cannot distinguish between sequential and concurrent drain since no timeout pressure exists.
    // It serves as a regression guard for the refactored structure.
    [SkipOnWindowsFact("bash not available on Windows — test uses Linux-specific pipe semantics")]
    public async Task RunProcessAsync_NormalPath_PipeDrainConcurrent_BothPipesComplete()
    {
        // Arrange: spawn a process that writes to both stdout and stderr then exits cleanly
        var validator = new ProcessExposingValidator();
        var script = "echo 'hello_stdout'; echo 'hello_stderr' >&2";

        // Act
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));

        // Assert: both streams captured correctly
        exitCode.Should().Be(0);
        stdout.Trim().Should().Be("hello_stdout");
        stderr.Trim().Should().Be("hello_stderr");
    }

    // TODO: This test does not distinguish old sequential code from the new concurrent code.
    // The catch-block fallback preserved completed pipes in both patterns. To truly validate
    // the fix, add a test where stdout completes at time X (0 < X < timeout) and stderr
    // completes at time Y where Y > timeout−X but Y < timeout (e.g., timeout=5s, stdout at 3s,
    // stderr at 4s). Old sequential code would lose stderr; new concurrent code preserves it.
    [SkipOnWindowsFact("bash not available on Windows — test uses Linux grandchild pipe-inheritance semantics")]
    public async Task RunProcessAsync_NormalPath_PipeDrainTimeout_PreservesCompletedPipe()
    {
        var pipeDrainTimeout = TimeSpan.FromSeconds(20);
        var validator = new ProcessExposingValidator(pipeDrainTimeout);

        // Script:
        //   1. Fork grandchild A that holds stdout open forever (sleep 300, inherits stdout)
        //   2. Fork grandchild B that holds stderr open for ~2s then exits (releases stderr)
        //   3. Write expected content to both pipes from main process
        //   4. Exit main process immediately — triggers pipe drain path
        // Grandchild A: inherits stdout (no redirect), closes stderr (2>/dev/null)
        // Grandchild B: inherits stderr (no redirect), closes stdout (1>/dev/null), sleeps 2s then exits
        var script = "(sleep 300 2>/dev/null &); (sleep 2 1>/dev/null &); echo 'expected_stdout'; echo 'expected_stderr' >&2; exit 0";

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(60));
        stderr.Trim().Should().Be("expected_stderr");
        exitCode.Should().Be(0);
    }

    // TODO: This test does not distinguish old sequential code from new concurrent code.
    [SkipOnWindowsFact("bash not available on Windows — test uses Linux grandchild pipe-inheritance semantics")]
    public async Task RunProcessAsync_NormalPath_PipeDrainTimeout_CompletesWithinBoundedTime()
    {
        var pipeDrainTimeout = TimeSpan.FromSeconds(5);
        var validator = new ProcessExposingValidator(pipeDrainTimeout);

        // Script: fork a grandchild that inherits both pipes and sleeps forever, then exit.
        var script = "sleep 300 & exit 0";

        // Act
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (exitCode, stdout, stderr) = await validator.RunProcessPublicAsync(
            "bash", $"-c \"{script}\"", Directory.GetCurrentDirectory(), CancellationToken.None, TimeSpan.FromSeconds(30));
        sw.Stop();

        sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(4)); // must actually wait for timeout
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(30));   // but not 2x timeout
        exitCode.Should().Be(0);
    }

    // ── Helper: exposes protected RunProcessAsync for direct testing ─────

    private sealed class ProcessExposingValidator : QualityGateValidator
    {
        private readonly TimeSpan? _pipeDrainTimeout;

        public ProcessExposingValidator(TimeSpan? pipeDrainTimeout = null) : base(Serilog.Log.Logger)
        {
            _pipeDrainTimeout = pipeDrainTimeout;
        }

        protected override TimeSpan PipeDrainTimeout => _pipeDrainTimeout ?? base.PipeDrainTimeout;

        public Task<(int ExitCode, string Stdout, string Stderr)> RunProcessPublicAsync(
            string fileName, string arguments, string workingDirectory, CancellationToken ct, TimeSpan timeout)
            => RunProcessAsync(fileName, arguments, workingDirectory, ct, timeout);
    }
}

/// <summary>
/// Custom xUnit v2-compatible FactAttribute that skips the test on Windows.
/// xUnit v2.9.x does not have Assert.Skip (that's a v3 feature), so a custom attribute
/// subclassing FactAttribute is the idiomatic way to do conditional platform skipping.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class SkipOnWindowsFact : FactAttribute
{
    public SkipOnWindowsFact(string reason = "Not supported on Windows")
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            Skip = reason;
    }
}
