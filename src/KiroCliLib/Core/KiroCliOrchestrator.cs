using KiroCliLib.Configuration;
using KiroCliLib.Models;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Orchestrates the complete Kiro CLI execution workflow.
/// </summary>
public class KiroCliOrchestrator : IKiroCliOrchestrator
{
    private readonly Configuration.Configuration _config;
    private readonly CallbackHandler _callbackHandler;
    private readonly ILogger _logger;
    private volatile ProcessWrapper? _activeProcess;

    public bool IsExecuting => _activeProcess != null;
    public int? ActiveProcessId
    {
        get
        {
            try { return _activeProcess?.IsRunning == true ? _activeProcess.ProcessId : null; }
            catch { return null; }
        }
    }
    public bool? IsActiveProcessAlive
    {
        get
        {
            var p = _activeProcess;
            if (p == null) return null;
            try { return p.IsRunning; }
            catch { return null; }
        }
    }
    public DateTime? LastOutputTime
    {
        get
        {
            var p = _activeProcess;
            return p != null ? p.LastOutputTime : null;
        }
    }

    public KiroCliOrchestrator(Configuration.Configuration config, CallbackHandler callbackHandler, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(callbackHandler);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _callbackHandler = callbackHandler;
        _logger = logger;
    }

    public async Task<int> ExecutePromptAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        using var processWrapper = new ProcessWrapper(_config, _logger);
        _activeProcess = processWrapper;
        var outputParser = new OutputParser();
        var fileSystemMonitor = new FileSystemMonitor();

        processWrapper.OutputReceived += (_, line) => { _logger.Information("Kiro: {Line}", AnsiStripper.Strip(line)); outputParser.ProcessLine(line); onOutputLine?.Invoke(line); };
        processWrapper.ErrorReceived += (_, line) => { _logger.Debug("Kiro (stderr): {Line}", AnsiStripper.Strip(line)); outputParser.ProcessLine(line); };
        outputParser.StateChanged += (_, newState) =>
        {
            _logger.Debug("State changed to: {State}", newState);
            _callbackHandler.Invoke(newState, new CallbackContext { State = newState, TestResults = outputParser.TestResults });
        };

        try
        {
            IReadOnlyList<FileSnapshot> beforeSnapshot;
            try { beforeSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to scan workspace before execution"); beforeSnapshot = Array.Empty<FileSnapshot>(); }

            var exitCode = await processWrapper.StartAsync(prompt, workspaceDirectory, useResume, cancellationToken);

            IReadOnlyList<FileChange> fileChanges;
            try
            {
                var afterSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory);
                fileChanges = fileSystemMonitor.CompareSnapshots(beforeSnapshot, afterSnapshot);
            }
            catch (Exception ex) { _logger.Warning(ex, "Failed to scan workspace after execution"); fileChanges = Array.Empty<FileChange>(); }

            _callbackHandler.Invoke(KiroState.Completed, new CallbackContext
            {
                State = KiroState.Completed,
                Message = fileChanges.Count > 0 ? $"{fileChanges.Count} file(s) changed" : "Execution completed successfully",
                Files = fileChanges.Select(fc => fc.Path).ToList(),
                FileChanges = fileChanges,
                TestResults = outputParser.TestResults,
                ExitCode = exitCode
            });
            return exitCode;
        }
        catch (TimeoutException ex)
        {
            _logger.Error(ex, "Kiro CLI execution timed out");
            _callbackHandler.Invoke(KiroState.Timeout, new CallbackContext { State = KiroState.Timeout, Message = $"Execution timed out after {_config.Timeout}" });
            return 124;
        }
        catch (OperationCanceledException ex)
        {
            _logger.Information(ex, "Kiro CLI execution was cancelled");
            _callbackHandler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error, Message = "Execution was cancelled by user" });
            return 130;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Kiro CLI execution failed");
            _callbackHandler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error, Message = $"Execution failed: {ex.Message}" });
            return 1;
        }
        finally
        {
            _activeProcess = null;
        }
    }
}
