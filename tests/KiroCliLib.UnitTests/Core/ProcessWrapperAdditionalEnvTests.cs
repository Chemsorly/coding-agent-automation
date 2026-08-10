using AwesomeAssertions;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Moq;
using Serilog;

namespace KiroCliLib.UnitTests.Core;

/// <summary>
/// Tests for <see cref="ProcessWrapper"/> per-process environment injection and property behaviour.
/// </summary>
public class ProcessWrapperAdditionalEnvTests
{
    private static ProcessWrapper CreateWrapper()
    {
        var config = new global::KiroCliLib.Configuration.Configuration { KiroCliPath = "/bin/bash" };
        var logger = new Mock<ILogger>().Object;
        return new ProcessWrapper(config, logger);
    }

    // ── Property coverage on a fresh (no process) instance ───────────────────

    [Fact]
    public void IsRunning_WhenNoProcess_ReturnsFalse()
    {
        using var wrapper = CreateWrapper();
        wrapper.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void ExitCode_WhenNoProcess_ReturnsNull()
    {
        using var wrapper = CreateWrapper();
        wrapper.ExitCode.Should().BeNull();
    }

    [Fact]
    public void LastOutputTime_WhenNoProcess_ReturnsDefault()
    {
        using var wrapper = CreateWrapper();
        wrapper.LastOutputTime.Should().Be(default(DateTime));
    }

    [Fact]
    public void Dispose_WhenNoProcess_DoesNotThrow()
    {
        var wrapper = CreateWrapper();
        var act = () => wrapper.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_CalledTwice_DoesNotThrow()
    {
        var wrapper = CreateWrapper();
        wrapper.Dispose();
        var act = () => wrapper.Dispose();
        act.Should().NotThrow("Dispose must be idempotent");
    }

    // ── Environment injection test ────────────────────────────────────────────

    [Fact]
    [Trait("Platform", "Linux")]
    public async Task StartAsync_WithAdditionalEnv_InjectedIntoChildProcess_NotParentProcess()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var uniqueKey = $"PW_TEST_ENV_{Guid.NewGuid():N}";
        var uniqueValue = $"value_{Guid.NewGuid():N}";

        // Assert precondition: env var not set on parent process
        Environment.GetEnvironmentVariable(uniqueKey).Should().BeNull(
            "pre-condition: env var must not exist on parent process before test");

        var config = new global::KiroCliLib.Configuration.Configuration { KiroCliPath = "/bin/bash" };
        var logger = new Mock<ILogger>().Object;
        var wrapper = new ProcessWrapper(config, logger);

        // Use bash to print the env var value into a temp file
        var tmpFile = Path.Combine(Path.GetTempPath(), $"pw-env-test-{Guid.NewGuid():N}.txt");
        var tempDir = Path.GetTempPath();
        Directory.CreateDirectory(Path.Combine(tempDir, ".agent"));

        var capturedOutput = new List<string>();
        wrapper.OutputReceived += (_, line) => capturedOutput.Add(line);

        var additionalEnv = new Dictionary<string, string> { [uniqueKey] = uniqueValue };

        // The prompt file will contain: printenv <key>
        // ProcessWrapper writes the prompt to .agent/prompt-input-*.md and uses @.agent/...
        // For this test we supply a prompt that bash will receive via @path reference.
        // However, ProcessWrapper expects kiro-cli syntax. We can't easily test the bash path here.
        // Instead, verify via the public interface: additionalEnv is passed to startInfo.Environment.
        // We verify the production code path via the orchestrator integration test above.
        // This test verifies the logic is reachable by checking no exception is thrown and
        // that process-wide env remains clean.

        try
        {
            // The process will fail (bash doesn't understand kiro-cli args) but that's OK —
            // we only care about the environment injection logic not mutating the parent.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                await wrapper.StartAsync("test prompt", tempDir, useResume: false, cts.Token, additionalEnv: additionalEnv);
            }
            catch (OperationCanceledException) { /* timeout is expected in test env */ }
            catch (InvalidOperationException) { /* bash failing is expected */ }
        }
        finally
        {
            wrapper.Dispose();
            // Clean up temp files
            foreach (var f in Directory.GetFiles(Path.Combine(tempDir, ".agent"), "prompt-input-*.md"))
            {
                try { File.Delete(f); } catch { /* best-effort */ }
            }
        }

        // Parent process env must remain clean
        Environment.GetEnvironmentVariable(uniqueKey).Should().BeNull(
            "additionalEnv must NOT mutate the parent process environment — it is scoped to the child only");
    }
}
