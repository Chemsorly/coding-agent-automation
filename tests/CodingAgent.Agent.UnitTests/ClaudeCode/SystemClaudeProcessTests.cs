using System.Diagnostics;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Agent.ClaudeCode;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

/// <summary>
/// Runs <see cref="SystemClaudeProcess"/> against /bin/sh standing in for the CLI.
/// Returns early on Windows (no /bin/sh), like KiroCliAgentProviderProcessTests.
/// </summary>
public class SystemClaudeProcessTests
{
    private static ProcessStartInfo Shell(string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return startInfo;
    }

    [Fact]
    public async Task Run_DeliversStdinStdoutStderrAndExitCode()
    {
        if (OperatingSystem.IsWindows())
            return;

        var stdout = new List<string>();
        var stderr = new List<string>();
        using var process = SystemClaudeProcessLauncher.Instance.Start(
            Shell("read line; echo \"got:$line\"; echo oops >&2; exit 3"), stdout.Add, stderr.Add);

        await process.WriteStdinAndCloseAsync("hello ü\n", CancellationToken.None);
        var exitCode = await process.WaitForExitAsync(CancellationToken.None);

        exitCode.Should().Be(3);
        stdout.Should().Equal("got:hello ü");
        stderr.Should().Equal("oops");
        process.IsRunning.Should().BeFalse();
        process.LastOutputTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task Terminate_SendsSigterm_WhichTheProcessCanHandle()
    {
        if (OperatingSystem.IsWindows())
            return;

        var stdout = new List<string>();
        using var process = SystemClaudeProcessLauncher.Instance.Start(
            Shell("trap 'echo saved; exit 0' TERM; echo ready; while :; do sleep 0.1; done"),
            line => { lock (stdout) stdout.Add(line); }, _ => { });
        await WaitForAsync(() => { lock (stdout) return stdout.Contains("ready"); });

        process.Terminate().Should().BeTrue();
        var exitCode = await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

        exitCode.Should().Be(0, "the process exits through its own TERM handler, not a kill");
        lock (stdout)
            stdout.Should().Contain("saved");
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("Condition not met within 10 s.");
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Kill_EndsARunningProcess()
    {
        if (OperatingSystem.IsWindows())
            return;

        using var process = SystemClaudeProcessLauncher.Instance.Start(Shell("sleep 30"), _ => { }, _ => { });
        process.IsRunning.Should().BeTrue();
        process.ProcessId.Should().NotBeNull();

        process.Kill();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await process.WaitForExitAsync(cts.Token);

        process.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Start_MissingExecutable_Throws()
    {
        var startInfo = new ProcessStartInfo { FileName = Path.Combine(Path.GetTempPath(), $"no-claude-{Guid.NewGuid():N}"), UseShellExecute = false };

        var start = () => SystemClaudeProcessLauncher.Instance.Start(startInfo, _ => { }, _ => { });

        start.Should().Throw<System.ComponentModel.Win32Exception>();
    }
}
