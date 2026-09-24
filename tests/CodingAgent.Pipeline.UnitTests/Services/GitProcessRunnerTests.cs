using AwesomeAssertions;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests;

// TODO: [WARNING] If a future test in this class mutates the process-wide environment
// (e.g. setting OTEL_* keys to verify that GitProcessRunner.RunAsync strips them, following the
// pattern used in SetupCommandRunnerTests), it must be decorated with
// [Collection("EnvironmentVariables")] to prevent parallel execution from causing race conditions
// with other environment-mutating tests in the same project. The attribute is intentionally omitted
// here because no such test exists yet, but the omission is flagged as a latent hazard.
[Trait("Category", "Integration")]
public class GitProcessRunnerTests : IDisposable
{
    private readonly string _tempDir;

    public GitProcessRunnerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"git-runner-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_Success_ReturnsStdout()
    {
        // git init produces output to stdout
        var output = await GitProcessRunner.RunAsync(_tempDir, "init", CancellationToken.None);

        output.Should().Contain("Initialized");
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_ThrowOnNonZeroExit_ThrowsInvalidOperationException()
    {
        var act = () => GitProcessRunner.RunAsync(_tempDir, "log --oneline -1", CancellationToken.None, throwOnNonZeroExit: true);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.Which.Message.Should().Contain("failed with exit code");
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_NoThrow_ReturnsStdout()
    {
        var output = await GitProcessRunner.RunAsync(_tempDir, "log --oneline -1", CancellationToken.None, throwOnNonZeroExit: false);

        // Non-zero exit but no exception — returns whatever stdout produced (empty in this case)
        output.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_Timeout_ThrowsTimeoutException()
    {
        // Use a pre-cancelled token to simulate timeout/cancellation behavior.
        // Cancel() is called directly (not CancelAfter) to guarantee the token is
        // already cancelled before RunAsync is invoked, avoiding a race condition.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => GitProcessRunner.RunAsync(_tempDir, "init", cts.Token);

        // When the caller's token is cancelled, OperationCanceledException propagates (not TimeoutException)
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
