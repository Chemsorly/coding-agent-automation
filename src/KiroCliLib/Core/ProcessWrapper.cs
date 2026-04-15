using System.Diagnostics;
using KiroCliLib.Configuration;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Manages the Kiro CLI process lifecycle with WSL integration support.
/// </summary>
public class ProcessWrapper : IDisposable
{
    private readonly Configuration.Configuration _config;
    private readonly ILogger _logger;
    private readonly bool _useWsl;
    private Process? _process;
    private bool _disposed;
    private DateTime _lastOutputTime;

    public event EventHandler<string>? OutputReceived;
    public event EventHandler<string>? ErrorReceived;
    public event EventHandler<int>? ProcessExited;

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;
    public DateTime LastOutputTime => _lastOutputTime;

    public ProcessWrapper(Configuration.Configuration config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _useWsl = config.UseWsl && OperatingSystem.IsWindows();
    }

    public async Task StartInteractiveAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        if (_process != null)
            throw new InvalidOperationException("Process is already running. Call Kill() first.");

        var startInfo = new ProcessStartInfo
        {
            FileName = _useWsl ? "wsl" : _config.KiroCliPath,
            Arguments = _useWsl ? $"{_config.KiroCliPath} chat --no-interactive" : "chat --no-interactive",
            WorkingDirectory = workspaceDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.Information("Starting Kiro CLI in interactive mode: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException("Failed to start Kiro CLI process.");
            _logger.Information("Kiro CLI process started with PID: {ProcessId}", _process.Id);
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            await Task.Delay(1000, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error starting Kiro CLI process");
            Kill();
            throw new InvalidOperationException("Failed to start Kiro CLI process", ex);
        }
    }

    public async Task SendPromptAsync(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        if (_process == null || _process.HasExited)
            throw new InvalidOperationException("Process is not running.");

        _lastOutputTime = DateTime.UtcNow;
        await _process.StandardInput.WriteLineAsync(prompt);
        await _process.StandardInput.FlushAsync();
    }

    public async Task<bool> WaitForResponseAsync(TimeSpan silenceDuration, TimeSpan maxWaitTime, CancellationToken cancellationToken)
    {
        if (_process == null || _process.HasExited) return false;
        var startTime = DateTime.UtcNow;

        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTime.UtcNow - _lastOutputTime >= silenceDuration) return true;
            await Task.Delay(100, cancellationToken);
        }
        return false;
    }

    public async Task<int> StartAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        if (_process != null)
            throw new InvalidOperationException("Process is already running. Call Kill() first.");

        var escapedPrompt = prompt.Replace("\"", "\\\"");
        var kiroArgs = useResume
            ? $"chat --no-interactive --resume --trust-all-tools \"{escapedPrompt}\""
            : $"chat --no-interactive --trust-all-tools \"{escapedPrompt}\"";

        var startInfo = new ProcessStartInfo
        {
            FileName = _useWsl ? "wsl" : _config.KiroCliPath,
            Arguments = _useWsl ? $"{_config.KiroCliPath} {kiroArgs}" : kiroArgs,
            WorkingDirectory = workspaceDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.Debug("Starting Kiro CLI: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException("Failed to start Kiro CLI process.");
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            var processTask = _process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(_config.Timeout, cancellationToken);
            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                Kill();
                throw new TimeoutException($"Kiro CLI process exceeded timeout of {_config.Timeout}");
            }

            var exitCode = _process.ExitCode;
            _process.CancelOutputRead();
            _process.CancelErrorRead();
            await Task.Delay(100, CancellationToken.None);
            return exitCode;
        }
        catch (OperationCanceledException) { Kill(); throw; }
        catch (TimeoutException) { throw; }
        catch (Exception ex) { Kill(); throw new InvalidOperationException("Failed to execute Kiro CLI process", ex); }
    }

    public void Kill()
    {
        if (_process == null || _process.HasExited) return;
        try
        {
            if (_useWsl)
            {
                try
                {
                    var killProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "wsl", Arguments = "pkill -9 -f kiro-cli",
                            UseShellExecute = false, CreateNoWindow = true,
                            RedirectStandardOutput = true, RedirectStandardError = true
                        }
                    };
                    killProcess.Start();
                    killProcess.WaitForExit(2000);
                }
                catch (Exception ex) { _logger.Warning(ex, "Failed to kill WSL kiro-cli processes"); }
            }
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000);
        }
        catch (Exception ex) { _logger.Error(ex, "Error killing Kiro CLI process"); }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null) { _lastOutputTime = DateTime.UtcNow; OutputReceived?.Invoke(this, e.Data); }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null) { _lastOutputTime = DateTime.UtcNow; ErrorReceived?.Invoke(this, e.Data); }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_process != null) ProcessExited?.Invoke(this, _process.ExitCode);
    }

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        if (_process != null)
        {
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
