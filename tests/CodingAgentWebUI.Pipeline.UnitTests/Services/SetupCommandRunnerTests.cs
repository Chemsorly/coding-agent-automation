using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests;

[Trait("Category", "Integration")]
[Trait("Platform", "Linux")]
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
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task RunAsync_SuccessfulCommand_ReturnsSuccessAndEmitsOutput()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo hello", "Test Step", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.FailureMessage.Should().BeNull();
        result.Exception.Should().BeNull();
        _emittedLines.Should().Contain(line => line.Contains("hello"));
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_ReturnsFailureWithExitCodeAndStderr()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo 'some error' >&2; exit 5", "Auth check", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Auth check");
        result.FailureMessage.Should().Contain("5");
        result.FailureMessage.Should().Contain("some error");
        result.Exception.Should().BeNull();
    }

    [Fact]
    public async Task RunAsync_NonZeroExitCode_TruncatesStderrTo500Chars()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();
        var longError = new string('x', 600);

        // Act
        var result = await SetupCommandRunner.RunAsync(
            $"echo '{longError}' >&2; exit 1", "Long Error Step", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        // The failure message should contain the truncated stderr (≤500 chars of the error content)
        // TODO: Strengthen truncation assertion — assert failure message length is bounded or contains exactly
        // the first 500 chars of error content, rather than only checking that the full 600-char string is absent.
        result.FailureMessage.Should().Contain("Long Error Step");
        result.FailureMessage.Should().Contain("exit code 1");
        // Full 600 chars should not appear in the failure message
        result.FailureMessage.Should().NotContain(longError);
    }

    [Fact]
    public async Task RunAsync_SecretsInjectedIntoProcessEnvironment()
    {
        // Arrange
        var secrets = new Dictionary<string, string>
        {
            ["MY_SECRET_KEY"] = "secret-value-1234"
        };

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo $MY_SECRET_KEY", "Secret Test", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        // The secret value should be masked in emitted output
        _emittedLines.Should().NotContain(line => line.Contains("secret-value-1234"));
        _emittedLines.Should().Contain(line => line.Contains("***"));
    }

    [Fact]
    public async Task RunAsync_SecretValuesMaskedInOutput()
    {
        // Arrange
        var secrets = new Dictionary<string, string>
        {
            ["TOKEN"] = "my-secret-token"
        };

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo 'The token is my-secret-token here'", "Mask Test", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _emittedLines.Should().NotContain(line => line.Contains("my-secret-token"));
        _emittedLines.Should().Contain(line => line.Contains("***"));
    }

    [Fact]
    public async Task RunAsync_SecretValuesMaskedInFailureMessage()
    {
        // Arrange
        var secrets = new Dictionary<string, string>
        {
            ["API_KEY"] = "super-secret-key"
        };

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo 'Error: super-secret-key is invalid' >&2; exit 1", "Secret Failure", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureMessage.Should().NotContain("super-secret-key");
        result.FailureMessage.Should().Contain("***");
    }

    [Fact]
    public async Task RunAsync_CancellationTokenRespected_ThrowsOperationCanceledException()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act
        var act = () => SetupCommandRunner.RunAsync(
            "sleep 10", "Cancelled Step", _tempDir, secrets,
            line => _emittedLines.Add(line), cts.Token);

        // Assert
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task RunAsync_ExceptionDuringProcessStart_ReturnsFailureWithException()
    {
        // Arrange — use invalid working directory to cause an exception
        var secrets = new Dictionary<string, string>();
        var invalidDir = Path.Combine(_tempDir, "nonexistent-" + Guid.NewGuid().ToString("N"));

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo hello", "Bad Dir Step", invalidDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("Bad Dir Step");
        result.FailureMessage.Should().Contain("threw an exception");
        result.Exception.Should().NotBeNull();
    }

    [Fact]
    public async Task RunAsync_EmptySecrets_StillRunsSuccessfully()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo works", "Empty Secrets", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        _emittedLines.Should().Contain(line => line.Contains("works"));
    }

    [Fact]
    public async Task RunAsync_StderrOutput_IsEmitted()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act
        var result = await SetupCommandRunner.RunAsync(
            "echo 'stderr output' >&2", "Stderr Step", _tempDir, secrets,
            line => _emittedLines.Add(line), CancellationToken.None);

        // Assert — command exits 0 even with stderr output
        result.Success.Should().BeTrue();
        _emittedLines.Should().Contain(line => line.Contains("stderr output"));
    }

    [Fact]
    public async Task RunAsync_NullEmitOutput_ThrowsArgumentNullException()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act
        var act = () => SetupCommandRunner.RunAsync(
            "echo hello", "Null Test", _tempDir, secrets,
            null!, CancellationToken.None);

        // Assert
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsFailureWithTimeoutMessage()
    {
        // Arrange
        var secrets = new Dictionary<string, string>();

        // Act — use the internal overload with a short timeout for testability
        var result = await SetupCommandRunner.RunAsync(
            "sleep 999", "Slow Step", _tempDir, secrets,
            _ => { }, CancellationToken.None, timeout: TimeSpan.FromSeconds(2));

        // Assert
        result.Success.Should().BeFalse();
        result.FailureMessage.Should().Contain("timed out");
        result.FailureMessage.Should().Contain("Slow Step");
        result.Exception.Should().BeNull();
    }

    // TODO: This test may not reliably exercise the IOException fault path on Linux.
    // After Kill(), the pipe read-end receives EOF (completing ReadToEndAsync successfully)
    // rather than throwing IOException. To truly verify exception observation, use a mock/wrapper
    // around Process that forces IOException on the pipe-read tasks.
    [Fact]
    public async Task RunAsync_Timeout_DoesNotProduceUnobservedTaskException()
    {
        // Arrange — track any unobserved task exceptions that surface during GC
        var unobservedExceptions = new List<Exception>();
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, e) =>
        {
            unobservedExceptions.Add(e.Exception);
            e.SetObserved();
        };

        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            // Act — write to both stdout and stderr to ensure both ReadToEndAsync tasks are active when killed
            var result = await SetupCommandRunner.RunAsync(
                "echo 'stdout data'; echo 'stderr data' >&2; sleep 999", "Hang Step", _tempDir,
                new Dictionary<string, string>(),
                _ => { }, CancellationToken.None, timeout: TimeSpan.FromSeconds(2));

            result.Success.Should().BeFalse();

            // Wait for pipe-read tasks to fault after process kill
            await Task.Delay(TimeSpan.FromSeconds(2));

            // Force GC to finalize abandoned tasks and trigger UnobservedTaskException if any
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            await Task.Delay(100);

            // Assert
            unobservedExceptions.Should().BeEmpty(
                "Abandoned stdout/stderr tasks should have their exceptions observed by ContinueWith");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}
