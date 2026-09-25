using System.Diagnostics;
using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using CodingAgent.Pipeline.Models;
using CodingAgent.Agent;
using CodingAgent.Agent.KiroCli;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Tests for process-spawning methods in KiroCliAgentProvider via IProcessStarter abstraction.
/// These tests spawn real /bin/sh processes and only run on Linux/macOS (CI environment).
/// </summary>
[Collection("EnvironmentVariables")]
public class KiroCliAgentProviderProcessTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IProcessStarter> _mockProcessStarter = new();

    private KiroCliAgentProvider CreateProvider(string? model = null, AgentEffortLevel effort = AgentEffortLevel.Auto) =>
        new(_mockOrchestrator.Object, _mockLogger.Object, model, "/usr/bin/fake", effort, _mockProcessStarter.Object);

    /// <summary>Starts a real process with controlled stdout, stderr, and exit code (cross-platform).</summary>
    private static Process StartShellProcess(string stdout = "", string stderr = "", int exitCode = 0)
    {
        ProcessStartInfo psi;
        if (OperatingSystem.IsWindows())
        {
            // On Windows, write stdout/stderr to temp files and type them to avoid cmd.exe escaping issues
            var stdoutFile = Path.GetTempFileName();
            var stderrFile = Path.GetTempFileName();
            File.WriteAllText(stdoutFile, stdout);
            File.WriteAllText(stderrFile, stderr);
            var script = $"type \"{stdoutFile}\" & type \"{stderrFile}\" 1>&2 & del \"{stdoutFile}\" & del \"{stderrFile}\" & exit /b {exitCode}";
            psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"{script}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        else
        {
            // On Linux/macOS, use /bin/sh with base64 encoding to avoid quoting issues
            var stdoutB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stdout));
            var stderrB64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(stderr));
            var script = $"printf '%s' \"$(echo {stdoutB64} | base64 -d)\"; printf '%s' \"$(echo {stderrB64} | base64 -d)\" >&2; exit {exitCode}";
            psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", script },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
        }
        return Process.Start(psi)!;
    }

    // ─── ValidateAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_ExitZero_DoesNotThrow()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(exitCode: 0));

        var provider = CreateProvider();
        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ValidateAsync_NonZeroWithStderr_ThrowsWithStderrMessage()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stderr: "auth failed", exitCode: 1));

        var provider = CreateProvider();
        var act = () => provider.ValidateAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*auth failed*");
    }

    [Fact]
    public async Task ValidateAsync_NonZeroWithEmptyStderr_ThrowsWithStdoutMessage()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: "check failed", stderr: "", exitCode: 1));

        var provider = CreateProvider();
        var act = () => provider.ValidateAsync(CancellationToken.None);

        var ex = await act.Should().ThrowAsync<InvalidOperationException>();
        ex.WithMessage("*check failed*");
    }

    [Fact]
    public async Task ValidateAsync_NullProcess_ThrowsInvalidOperationException()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns((Process?)null);

        var provider = CreateProvider();
        var act = () => provider.ValidateAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Failed to start*");
    }

    [Fact]
    public async Task ValidateAsync_CancellationRequested_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Process starts but cancellation is already requested — WaitForExitAsync/ReadToEndAsync will throw
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(exitCode: 0));

        var provider = CreateProvider();
        var act = () => provider.ValidateAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ─── GetLatestSessionIdAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetLatestSessionIdAsync_NullProcess_ReturnsNull()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns((Process?)null);

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_EmptyOutput_ReturnsNull()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: ""));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_CommentLinesSkipped_ReturnsNull()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: "# This is a comment\n# Another comment\n"));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_SessionHeaderSkipped_ReturnsNull()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: "Session ID  Created  Status\n"));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_ShortToken_Skipped()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: "abc\n"));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_ValidSessionId_ReturnsId()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Returns(() => StartShellProcess(stdout: "# Sessions\nabcdef12-3456-7890-abcd-ef1234567890 2024-01-01\n"));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().Be("abcdef12-3456-7890-abcd-ef1234567890");
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_ExceptionThrown_LogsWarningAndReturnsNull()
    {
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
            .Throws(new IOException("process error"));

        var provider = CreateProvider();
        var result = await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

        result.Should().BeNull();
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<WorkspacePath>()), Times.Once);
    }

    // ─── OTEL env-var stripping ──────────────────────────────────────────

    /// <summary>
    /// Uses Moq Callback to capture the ProcessStartInfo before the mock "starts" the process,
    /// then asserts that ChildProcessEnvironment.StripTelemetry ran before the call.
    /// The test sets OTEL_* keys in the parent process so they land in the PSI copy.
    /// </summary>
    [Fact]
    public async Task ValidateAsync_DoesNotPassOtelEnvVarsToChildProcess()
    {
        const string otelKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
        var previous = Environment.GetEnvironmentVariable(otelKey);
        Environment.SetEnvironmentVariable(otelKey, "http://grafana.example.com:4317");
        try
        {
            ProcessStartInfo? captured = null;
            _mockProcessStarter
                .Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
                .Callback<ProcessStartInfo>(psi => captured = psi)
                .Returns(() => StartShellProcess(exitCode: 0));

            var provider = CreateProvider();
            await provider.ValidateAsync(CancellationToken.None);

            captured.Should().NotBeNull("the process starter must have been called");
            // OTEL key must have been stripped before Start() was called
            captured!.Environment.ContainsKey(otelKey).Should().BeFalse(
                "OTEL vars must be stripped before handing PSI to IProcessStarter");
            // Non-telemetry content is still present (proves strip ran, not a no-op)
            captured.Environment.ContainsKey("PATH").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(otelKey, previous);
        }
    }

    [Fact]
    public async Task GetLatestSessionIdAsync_DoesNotPassOtelEnvVarsToChildProcess()
    {
        const string otelKey = "OTEL_SERVICE_NAME";
        var previous = Environment.GetEnvironmentVariable(otelKey);
        Environment.SetEnvironmentVariable(otelKey, "coding-agent-worker-test");
        try
        {
            ProcessStartInfo? captured = null;
            _mockProcessStarter
                .Setup(p => p.Start(It.IsAny<ProcessStartInfo>()))
                .Callback<ProcessStartInfo>(psi => captured = psi)
                .Returns(() => StartShellProcess(stdout: "session-id-123456789\n", exitCode: 0));

            var provider = CreateProvider();
            await provider.GetLatestSessionIdAsync("/workspace", CancellationToken.None);

            captured.Should().NotBeNull("the process starter must have been called");
            captured!.Environment.ContainsKey(otelKey).Should().BeFalse(
                "OTEL vars must be stripped before handing PSI to IProcessStarter");
            captured.Environment.ContainsKey("PATH").Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(otelKey, previous);
        }
    }

    // ─── GetHealthStatus ─────────────────────────────────────────────────

    [Fact]
    public void GetHealthStatus_MapsOrchestratorProperties()
    {
        _mockOrchestrator.Setup(o => o.IsExecuting).Returns(true);
        _mockOrchestrator.Setup(o => o.ActiveProcessId).Returns(1234);
        _mockOrchestrator.Setup(o => o.IsActiveProcessAlive).Returns(true);
        var lastOutput = new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _mockOrchestrator.Setup(o => o.LastOutputTime).Returns(lastOutput);

        var provider = CreateProvider();
        var status = provider.GetHealthStatus();

        status.IsExecuting.Should().BeTrue();
        status.ProcessId.Should().Be(1234);
        status.IsProcessAlive.Should().BeTrue();
        status.LastOutputTime.Should().Be(lastOutput);
    }

    // ─── ExecuteAsync external cancellation ──────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ExternalCancellation_PropagatesOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();

        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                async (_, _, _, ct, _, _, _) =>
                {
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                });

        var provider = CreateProvider();
        var request = new AgentRequest
        {
            Prompt = "test",
            WorkspacePath = "/ws",
            UseResume = true, // Use shared orchestrator so mock handles cancellation
            Timeout = TimeSpan.FromSeconds(30) // Long timeout — not the cause of cancellation
        };

        // Cancel externally after a short delay
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        var act = () => provider.ExecuteAsync(request, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
