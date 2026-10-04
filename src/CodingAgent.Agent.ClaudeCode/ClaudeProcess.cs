using System.ComponentModel;
using System.Diagnostics;
using Serilog;

namespace CodingAgent.Agent.ClaudeCode;

/// <summary>A running Claude Code CLI process.</summary>
internal interface IClaudeProcess : IDisposable
{
    int? ProcessId { get; }
    bool IsRunning { get; }

    /// <summary>When the process last wrote a line, starting at its start time.</summary>
    DateTime LastOutputTime { get; }

    /// <summary>Writes <paramref name="text"/> to stdin and closes it, so the CLI starts working.</summary>
    Task WriteStdinAndCloseAsync(string text, CancellationToken ct);

    /// <summary>Waits until the process has exited and all output lines were delivered.</summary>
    Task<int> WaitForExitAsync(CancellationToken ct);

    /// <summary>Kills the process and its children. No-op once it has exited.</summary>
    void Kill();
}

/// <summary>Starts Claude Code CLI processes. Seam for unit tests.</summary>
internal interface IClaudeProcessLauncher
{
    /// <summary>
    /// Starts the process. <paramref name="onStdoutLine"/> and <paramref name="onStderrLine"/> are
    /// called once per line, in order, on a background thread.
    /// </summary>
    IClaudeProcess Start(ProcessStartInfo startInfo, Action<string> onStdoutLine, Action<string> onStderrLine);
}

/// <summary>Launches real OS processes.</summary>
internal sealed class SystemClaudeProcessLauncher : IClaudeProcessLauncher
{
    public static readonly SystemClaudeProcessLauncher Instance = new();

    public IClaudeProcess Start(ProcessStartInfo startInfo, Action<string> onStdoutLine, Action<string> onStderrLine)
    {
        var process = new SystemClaudeProcess(startInfo, onStdoutLine, onStderrLine);
        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }
}

/// <summary>
/// <see cref="IClaudeProcess"/> over <see cref="Process"/>, reading stdout and stderr line by line.
/// </summary>
/// <remarks>
/// Like <c>KiroCliLib.Core.ProcessWrapper</c>, there is no OS-level guard against orphaned children
/// if the agent itself dies; the container's cgroup cleanup covers that in Kubernetes.
/// </remarks>
internal sealed class SystemClaudeProcess : IClaudeProcess
{
    private const int KillTimeoutMs = 5000;

    private readonly Process _process;
    private readonly Action<string> _onStdoutLine;
    private readonly Action<string> _onStderrLine;
    private long _lastOutputTicks;
    private bool _disposed;

    public SystemClaudeProcess(ProcessStartInfo startInfo, Action<string> onStdoutLine, Action<string> onStderrLine)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _onStdoutLine = onStdoutLine ?? throw new ArgumentNullException(nameof(onStdoutLine));
        _onStderrLine = onStderrLine ?? throw new ArgumentNullException(nameof(onStderrLine));
        _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
    }

    public int? ProcessId
    {
        get
        {
            try { return _process.Id; }
            catch (InvalidOperationException) { return null; }
        }
    }

    public bool IsRunning
    {
        get
        {
            try { return !_process.HasExited; }
            catch (InvalidOperationException) { return false; }
        }
    }

    public DateTime LastOutputTime => new(Interlocked.Read(ref _lastOutputTicks), DateTimeKind.Utc);

    internal void Start()
    {
        // Count the start as output, so a stall monitor does not judge a process that has not
        // printed anything yet by DateTime.MinValue.
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);

        if (!_process.Start())
            throw new InvalidOperationException($"Failed to start {_process.StartInfo.FileName}.");

        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public async Task WriteStdinAndCloseAsync(string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        var stdin = _process.StandardInput;
        await stdin.WriteAsync(text.AsMemory(), ct);
        await stdin.FlushAsync(ct);
        stdin.Close();
    }

    public async Task<int> WaitForExitAsync(CancellationToken ct)
    {
        await _process.WaitForExitAsync(ct);
        // The parameterless overload also waits for the asynchronous output handlers to drain.
        _process.WaitForExit();
        return _process.ExitCode;
    }

    public void Kill()
    {
        try
        {
            if (_process.HasExited)
                return;
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(KillTimeoutMs);
        }
        catch (InvalidOperationException)
        {
            // Not started, or already exited and released.
        }
        catch (Win32Exception ex)
        {
            Log.Warning(ex, "Failed to kill Claude Code CLI process");
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e) => Deliver(e.Data, _onStdoutLine);

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e) => Deliver(e.Data, _onStderrLine);

    private void Deliver(string? line, Action<string> handler)
    {
        if (line is null)
            return;
        Interlocked.Exchange(ref _lastOutputTicks, DateTime.UtcNow.Ticks);
        try
        {
            handler(line);
        }
        catch (Exception ex)
        {
            // These handlers run on a thread-pool thread; an escaping exception would end the agent.
            Log.Warning(ex, "Failed to handle a Claude Code CLI output line");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Kill();
        _process.OutputDataReceived -= OnOutputDataReceived;
        _process.ErrorDataReceived -= OnErrorDataReceived;
        _process.Dispose();
    }
}
