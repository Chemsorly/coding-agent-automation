using System.Diagnostics;
using KiroCliLib.Configuration;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Manages the Kiro CLI process lifecycle with WSL integration support.
/// </summary>
public class ProcessWrapper : IProcessWrapper
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

    public async Task<int> StartAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken, string? resumeSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);
        if (_process != null)
            throw new InvalidOperationException("Process is already running. Call Kill() first.");

        // Write prompt to a temporary file and use Kiro's @path file reference syntax.
        // File references expand file contents inline before sending to the model.
        // See: https://kiro.dev/docs/cli/chat/file-references/
        // This avoids shell argument escaping issues with complex prompts containing
        // quotes, newlines, backticks, and JSON that cause exit code 2 (argument parse error).
        var kiroDir = Path.Combine(workspaceDirectory, ".kiro");
        Directory.CreateDirectory(kiroDir);
        var promptFile = Path.Combine(kiroDir, "prompt-input.md");
        await File.WriteAllTextAsync(promptFile, prompt, cancellationToken);

        // The @path syntax expands file contents inline before sending (per Kiro docs).
        // Use explicit relative path (@./path) to avoid prompt name collision.
        var inlinePrompt = "@.kiro/prompt-input.md";
        var resumeFlag = resumeSessionId is not null
            ? $"--resume-id {resumeSessionId}"
            : useResume ? "--resume" : null;
        var kiroArgs = resumeFlag is not null
            ? $"chat --no-interactive {resumeFlag} --trust-all-tools \"{inlinePrompt}\""
            : $"chat --no-interactive --trust-all-tools \"{inlinePrompt}\"";

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

        _logger.Debug("Starting Kiro CLI: {FileName} {Arguments} (prompt written to {PromptFile}, {Length} chars)",
            startInfo.FileName, startInfo.Arguments, promptFile, prompt.Length);
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
        finally
        {
            // Clean up the prompt file
            try { File.Delete(promptFile); } catch { /* best-effort cleanup */ }
        }
    }

    /// <summary>
    /// Forcefully terminates the running process and its process tree.
    /// </summary>
    /// <remarks>
    /// This method is intentionally synchronous because process termination is immediate
    /// (not a graceful cancel-and-wait pattern). <see cref="GracefulShutdownHelper"/> is not
    /// applicable here since there is no CancellationTokenSource or awaitable Task to cancel —
    /// the process is killed directly via OS signals.
    /// </remarks>
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
