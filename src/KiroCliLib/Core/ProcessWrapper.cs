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

    public bool IsRunning => _process != null && !_process.HasExited;
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;
    public int? ProcessId { get { try { return _process?.Id; } catch { return null; } } }
    public DateTime LastOutputTime => _lastOutputTime;

    public ProcessWrapper(Configuration.Configuration config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _useWsl = config.UseWsl && OperatingSystem.IsWindows();
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
        _process.EnableRaisingEvents = true;

        try
        {
            if (!_process.Start())
                throw new InvalidOperationException("Failed to start Kiro CLI process.");
            _process.StandardInput.Close();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Wait for the process to exit. Timeout is controlled by the caller via
            // CancellationToken (e.g., KiroCliAgentProvider creates a CTS from AgentTimeout).
            // When the token fires, WaitForExitAsync throws OperationCanceledException,
            // which is caught below and triggers Kill().
            await _process.WaitForExitAsync(cancellationToken);

            var exitCode = _process.ExitCode;
            _process.CancelOutputRead();
            _process.CancelErrorRead();
            await Task.Delay(100, CancellationToken.None);
            return exitCode;
        }
        catch (OperationCanceledException) { Kill(); throw; }
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

    public void Dispose()
    {
        if (_disposed) return;
        Kill();
        if (_process != null)
        {
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnErrorDataReceived;
            _process.Dispose();
            _process = null;
        }
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
