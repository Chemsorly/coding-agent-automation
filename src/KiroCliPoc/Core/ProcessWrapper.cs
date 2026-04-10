using System.Diagnostics;
using KiroCliPoc.Configuration;
using KiroCliPoc.Models;
using Serilog;

namespace KiroCliPoc.Core;

/// <summary>
/// Manages the Kiro CLI process lifecycle with WSL integration support.
/// Handles process startup, output capture, timeout enforcement, and graceful termination.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS, DOTNET_PRINCIPLES, SECURITY_INPUT_VALIDATION, PERFORMANCE_RESOURCE_DISPOSAL
/// </remarks>
public class ProcessWrapper : IDisposable
{
    private readonly Configuration.Configuration _config;
    private readonly ILogger _logger;
    private readonly bool _useWsl;
    private Process? _process;
    private bool _disposed;
    private DateTime _lastOutputTime;

    /// <summary>
    /// Raised when a line is received from standard output.
    /// </summary>
    public event EventHandler<string>? OutputReceived;

    /// <summary>
    /// Raised when a line is received from standard error.
    /// </summary>
    public event EventHandler<string>? ErrorReceived;

    /// <summary>
    /// Raised when the process exits.
    /// </summary>
    public event EventHandler<int>? ProcessExited;

    /// <summary>
    /// Gets whether the process is currently running.
    /// </summary>
    public bool IsRunning => _process != null && !_process.HasExited;

    /// <summary>
    /// Gets the exit code of the process, or null if not yet exited.
    /// </summary>
    public int? ExitCode => _process?.HasExited == true ? _process.ExitCode : null;

    /// <summary>
    /// Gets the time of the last output received from the process.
    /// </summary>
    public DateTime LastOutputTime => _lastOutputTime;

    /// <summary>
    /// Initializes a new instance of the ProcessWrapper class.
    /// </summary>
    /// <param name="config">The configuration containing Kiro CLI settings.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when config or logger is null.</exception>
    public ProcessWrapper(Configuration.Configuration config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _logger = logger;
        _useWsl = config.UseWsl && OperatingSystem.IsWindows();
    }

    /// <summary>
    /// Starts the Kiro CLI process in interactive mode.
    /// </summary>
    /// <param name="workspaceDirectory">The workspace directory.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <exception cref="ArgumentNullException">Thrown when workspaceDirectory is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when process fails to start.</exception>
    public async Task StartInteractiveAsync(string workspaceDirectory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        if (_process != null)
        {
            throw new InvalidOperationException("Process is already running. Call Kill() first.");
        }

        // Build Kiro CLI arguments for interactive mode (no prompt, just start chat)
        var kiroArgs = "chat";

        // Configure process start info with WSL support
        var startInfo = new ProcessStartInfo
        {
            FileName = _useWsl ? "wsl" : _config.KiroCliPath,
            Arguments = _useWsl 
                ? $"{_config.KiroCliPath} {kiroArgs}"
                : kiroArgs,
            WorkingDirectory = workspaceDirectory,
            RedirectStandardInput = true,   // Keep stdin open for sending prompts
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.Information("Starting Kiro CLI in interactive mode: {FileName} {Arguments}", 
            startInfo.FileName, startInfo.Arguments);
        _logger.Information("Working directory: {WorkingDirectory}", startInfo.WorkingDirectory);

        // Create and configure process
        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;

        try
        {
            // Start the process
            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start Kiro CLI process.");
            }

            _logger.Information("Kiro CLI process started with PID: {ProcessId}", _process.Id);

            // Begin async output reading
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Give Kiro a moment to initialize
            await Task.Delay(1000, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error starting Kiro CLI process");
            Kill();
            throw new InvalidOperationException("Failed to start Kiro CLI process", ex);
        }
    }

    /// <summary>
    /// Sends a prompt to the running interactive Kiro CLI process.
    /// </summary>
    /// <param name="prompt">The prompt to send.</param>
    /// <exception cref="ArgumentNullException">Thrown when prompt is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when process is not running.</exception>
    public async Task SendPromptAsync(string prompt)
    {
        ArgumentNullException.ThrowIfNull(prompt);

        if (_process == null || _process.HasExited)
        {
            throw new InvalidOperationException("Process is not running. Call StartInteractiveAsync() first.");
        }

        try
        {
            _logger.Information("Sending prompt to Kiro CLI");
            _lastOutputTime = DateTime.UtcNow; // Reset timer when sending prompt
            await _process.StandardInput.WriteLineAsync(prompt);
            await _process.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error sending prompt to Kiro CLI");
            throw new InvalidOperationException("Failed to send prompt to Kiro CLI", ex);
        }
    }

    /// <summary>
    /// Waits for the response to complete by detecting when output stops.
    /// </summary>
    /// <param name="silenceDuration">Duration of silence to consider response complete.</param>
    /// <param name="maxWaitTime">Maximum time to wait for response.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>True if response completed, false if timed out.</returns>
    public async Task<bool> WaitForResponseAsync(
        TimeSpan silenceDuration, 
        TimeSpan maxWaitTime, 
        CancellationToken cancellationToken)
    {
        if (_process == null || _process.HasExited)
        {
            return false;
        }

        var startTime = DateTime.UtcNow;
        
        while (DateTime.UtcNow - startTime < maxWaitTime)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Check if we've had silence for the required duration
            var timeSinceLastOutput = DateTime.UtcNow - _lastOutputTime;
            if (timeSinceLastOutput >= silenceDuration)
            {
                _logger.Debug("Response complete after {Duration}s of silence", timeSinceLastOutput.TotalSeconds);
                return true;
            }

            // Check more frequently than the silence duration
            await Task.Delay(100, cancellationToken);
        }

        _logger.Warning("Response did not complete within {MaxWaitTime}s", maxWaitTime.TotalSeconds);
        return false;
    }

    /// <summary>
    /// Starts the Kiro CLI process with the specified prompt (single-shot mode).
    /// </summary>
    /// <param name="prompt">The prompt to send to Kiro CLI.</param>
    /// <param name="workspaceDirectory">The workspace directory.</param>
    /// <param name="useResume">Whether to use --resume flag to continue previous conversation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The exit code of the process.</returns>
    /// <exception cref="ArgumentNullException">Thrown when prompt is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown when process fails to start.</exception>
    /// <exception cref="OperationCanceledException">Thrown when operation is cancelled.</exception>
    public async Task<int> StartAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        if (_process != null)
        {
            throw new InvalidOperationException("Process is already running. Call Kill() first.");
        }

        // Escape quotes in the prompt for shell safety
        var escapedPrompt = prompt.Replace("\"", "\\\"");

        // Build Kiro CLI arguments with the prompt and optional --resume flag
        // Add --trust-all-tools to allow tool execution without confirmation
        var kiroArgs = useResume 
            ? $"chat --resume --trust-all-tools \"{escapedPrompt}\""
            : $"chat --trust-all-tools \"{escapedPrompt}\"";

        // Configure process start info with WSL support
        var startInfo = new ProcessStartInfo
        {
            FileName = _useWsl ? "wsl" : _config.KiroCliPath,
            Arguments = _useWsl 
                ? $"{_config.KiroCliPath} {kiroArgs}"
                : kiroArgs,
            WorkingDirectory = workspaceDirectory, // Keep Windows path for process start
            RedirectStandardInput = true,  // Redirect stdin to prevent TTY attachment
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _logger.Debug("Starting Kiro CLI process: {FileName} {Arguments}", 
            startInfo.FileName, startInfo.Arguments);
        _logger.Debug("Working directory: {WorkingDirectory}", startInfo.WorkingDirectory);

        // Create and configure process
        _process = new Process { StartInfo = startInfo };
        _process.OutputDataReceived += OnOutputDataReceived;
        _process.ErrorDataReceived += OnErrorDataReceived;
        _process.Exited += OnProcessExited;
        _process.EnableRaisingEvents = true;

        try
        {
            // Start the process
            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start Kiro CLI process.");
            }

            _logger.Debug("Kiro CLI process started with PID: {ProcessId}", _process.Id);

            // Close stdin immediately to prevent TTY attachment
            _process.StandardInput.Close();

            // Begin async output reading
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();

            // Wait for process completion with timeout and cancellation support
            var processTask = _process.WaitForExitAsync(cancellationToken);
            var timeoutTask = Task.Delay(_config.Timeout, cancellationToken);

            var completedTask = await Task.WhenAny(processTask, timeoutTask);

            if (completedTask == timeoutTask)
            {
                _logger.Warning("Kiro CLI process exceeded timeout of {Timeout}", _config.Timeout);
                Kill();
                throw new TimeoutException($"Kiro CLI process exceeded timeout of {_config.Timeout}");
            }

            // Process completed normally
            var exitCode = _process.ExitCode;
            _logger.Debug("Kiro CLI process exited with code: {ExitCode}", exitCode);

            // Cancel async read operations and wait for them to complete
            _process.CancelOutputRead();
            _process.CancelErrorRead();
            
            // Give a moment for cleanup
            await Task.Delay(100, CancellationToken.None);

            return exitCode;
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Kiro CLI process was cancelled");
            Kill();
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error starting or waiting for Kiro CLI process");
            Kill();
            throw new InvalidOperationException("Failed to execute Kiro CLI process", ex);
        }
    }

    /// <summary>
    /// Forcefully terminates the running process.
    /// </summary>
    public void Kill()
    {
        if (_process == null || _process.HasExited)
        {
            return;
        }

        try
        {
            _logger.Debug("Killing Kiro CLI process with PID: {ProcessId}", _process.Id);
            
            // For WSL processes, we need to kill the process inside WSL, not just the wrapper
            if (_useWsl)
            {
                try
                {
                    // Kill all kiro-cli processes in WSL to ensure cleanup
                    var killProcess = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "wsl",
                            Arguments = "pkill -9 -f kiro-cli",
                            UseShellExecute = false,
                            CreateNoWindow = true,
                            RedirectStandardOutput = true,
                            RedirectStandardError = true
                        }
                    };
                    killProcess.Start();
                    killProcess.WaitForExit(2000);
                    _logger.Debug("Killed WSL kiro-cli processes");
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to kill WSL kiro-cli processes");
                }
            }
            
            // Also kill the Windows wrapper process
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit(5000); // Wait up to 5 seconds for graceful exit
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Error killing Kiro CLI process");
        }
    }

    /// <summary>
    /// Converts a Windows path to a WSL path.
    /// </summary>
    /// <param name="windowsPath">The Windows path (e.g., C:\Projects\workspace).</param>
    /// <returns>The WSL path (e.g., /mnt/c/Projects/workspace).</returns>
    private static string ConvertToWslPath(string windowsPath)
    {
        if (string.IsNullOrWhiteSpace(windowsPath))
        {
            return windowsPath;
        }

        // Handle drive letter paths (C:\path -> /mnt/c/path)
        if (windowsPath.Length >= 2 && windowsPath[1] == ':')
        {
            var drive = char.ToLower(windowsPath[0]);
            var path = windowsPath.Substring(2).Replace('\\', '/');
            return $"/mnt/{drive}{path}";
        }

        // Just replace backslashes for relative paths
        return windowsPath.Replace('\\', '/');
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            _lastOutputTime = DateTime.UtcNow;
            _logger.Debug("[STDOUT] Received: {Data}", e.Data); // Debug to see if this fires
            OutputReceived?.Invoke(this, e.Data);
        }
    }

    private void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (e.Data != null)
        {
            _lastOutputTime = DateTime.UtcNow;
            ErrorReceived?.Invoke(this, e.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_process != null)
        {
            ProcessExited?.Invoke(this, _process.ExitCode);
        }
    }

    /// <summary>
    /// Disposes the process wrapper and releases all resources.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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
