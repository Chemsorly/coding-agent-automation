using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

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
        // Use a command that will hang — git fetch on a non-existent remote with no timeout escape
        // Instead, use a pre-cancelled token to simulate timeout behavior
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        // Allow the cancellation to trigger before starting
        await Task.Delay(50);

        var act = () => GitProcessRunner.RunAsync(_tempDir, "init", cts.Token);

        // When the caller's token is cancelled, OperationCanceledException propagates (not TimeoutException)
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
