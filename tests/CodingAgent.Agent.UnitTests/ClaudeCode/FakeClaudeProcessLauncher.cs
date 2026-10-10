using System.Diagnostics;
using CodingAgent.Agent.ClaudeCode;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

/// <summary>Scripted output of one fake Claude Code CLI run.</summary>
/// <param name="Hang">Prints nothing and runs until killed.</param>
/// <param name="KeepRunningAfterOutput">Prints its output, then runs until killed (a CLI waiting for background commands).</param>
internal sealed record FakeClaudeRun(
    IReadOnlyList<string> Stdout,
    int ExitCode = 0,
    IReadOnlyList<string>? Stderr = null,
    bool Hang = false,
    bool KeepRunningAfterOutput = false);

/// <summary>
/// <see cref="IClaudeProcessLauncher"/> that records what would be started and plays back
/// scripted stream-json output instead of running the CLI.
/// </summary>
internal sealed class FakeClaudeProcessLauncher : IClaudeProcessLauncher
{
    private readonly Queue<FakeClaudeRun> _runs = new();

    public List<ProcessStartInfo> Started { get; } = [];
    public List<string> Stdin { get; } = [];
    public List<FakeClaudeProcess> Processes { get; } = [];
    public Exception? StartException { get; set; }

    public FakeClaudeProcessLauncher Enqueue(FakeClaudeRun run)
    {
        _runs.Enqueue(run);
        return this;
    }

    public FakeClaudeProcessLauncher Enqueue(params string[] stdout) => Enqueue(new FakeClaudeRun(stdout));

    public IClaudeProcess Start(ProcessStartInfo startInfo, Action<string> onStdoutLine, Action<string> onStderrLine)
    {
        if (StartException is not null)
            throw StartException;

        Started.Add(startInfo);
        var run = _runs.Count > 0 ? _runs.Dequeue() : new FakeClaudeRun([]);
        var process = new FakeClaudeProcess(run, onStdoutLine, onStderrLine, Stdin);
        Processes.Add(process);
        return process;
    }
}

internal sealed class FakeClaudeProcess(
    FakeClaudeRun run, Action<string> onStdoutLine, Action<string> onStderrLine, List<string> stdin) : IClaudeProcess
{
    /// <summary>Exit code of a hanging run that was killed (128 + SIGKILL).</summary>
    public const int KilledExitCode = 137;

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _killed = new();

    public bool Killed { get; private set; }
    public bool Disposed { get; private set; }

    /// <summary>Completes once the provider waits for the process to exit.</summary>
    public Task Running => _started.Task;

    public int? ProcessId => 4242;
    public bool IsRunning => !Killed && (run.Hang || run.KeepRunningAfterOutput);
    public DateTime LastOutputTime { get; } = DateTime.UtcNow;

    public Task WriteStdinAndCloseAsync(string text, CancellationToken ct)
    {
        stdin.Add(text);
        return Task.CompletedTask;
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        _started.TrySetResult();
        if (run.Hang)
        {
            await WaitUntilKilledAsync(ct);
            return KilledExitCode;
        }

        foreach (var line in run.Stdout)
            onStdoutLine(line);
        foreach (var line in run.Stderr ?? [])
            onStderrLine(line);

        if (run.KeepRunningAfterOutput)
        {
            await WaitUntilKilledAsync(ct);
            return KilledExitCode;
        }
        return run.ExitCode;
    }

    /// <summary>Returns once the process is killed; throws when <paramref name="ct"/> is cancelled first.</summary>
    private async Task WaitUntilKilledAsync(CancellationToken ct)
    {
        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _killed.Token);
        try
        {
            await Task.Delay(Timeout.Infinite, waitCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Killed.
        }
    }

    public void Kill()
    {
        Killed = true;
        _killed.Cancel();
    }

    public void Dispose() => Disposed = true;
}
