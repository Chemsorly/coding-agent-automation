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
    private readonly Func<IProcessWrapper> _processWrapperFactory;
    private readonly Func<IOutputParser> _outputParserFactory;
    private readonly Func<IFileSystemMonitor> _fileSystemMonitorFactory;
    private volatile IProcessWrapper? _activeProcess;
    private bool _disposed;

    public bool IsExecuting => _activeProcess != null;
    public int? ActiveProcessId
    {
        get
        {
            var p = _activeProcess;
            if (p == null) return null;
            if (p is ProcessWrapper pw)
            {
                try { return pw.IsRunning ? pw.ProcessId : null; }
                catch { return null; }
            }
            return null;
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

    /// <summary>
    /// Creates a new orchestrator with explicit component factories for testability.
    /// </summary>
    /// <param name="config">The configuration for the CLI.</param>
    /// <param name="callbackHandler">The callback handler for state notifications.</param>
    /// <param name="logger">The logger instance.</param>
    /// <param name="processWrapperFactory">Factory to create <see cref="IProcessWrapper"/> instances.</param>
    /// <param name="outputParserFactory">Factory to create <see cref="IOutputParser"/> instances.</param>
    /// <param name="fileSystemMonitorFactory">Factory to create <see cref="IFileSystemMonitor"/> instances.</param>
    public KiroCliOrchestrator(
        Configuration.Configuration config,
        CallbackHandler callbackHandler,
        ILogger logger,
        Func<IProcessWrapper> processWrapperFactory,
        Func<IOutputParser> outputParserFactory,
        Func<IFileSystemMonitor> fileSystemMonitorFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(callbackHandler);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processWrapperFactory);
        ArgumentNullException.ThrowIfNull(outputParserFactory);
        ArgumentNullException.ThrowIfNull(fileSystemMonitorFactory);
        _config = config;
        _callbackHandler = callbackHandler;
        _logger = logger;
        _processWrapperFactory = processWrapperFactory;
        _outputParserFactory = outputParserFactory;
        _fileSystemMonitorFactory = fileSystemMonitorFactory;
    }

    /// <summary>
    /// Creates a new orchestrator with default concrete component implementations.
    /// Retained for backward compatibility.
    /// </summary>
    public KiroCliOrchestrator(Configuration.Configuration config, CallbackHandler callbackHandler, ILogger logger)
        : this(
            config,
            callbackHandler,
            logger,
            () => new ProcessWrapper(config, logger),
            () => new OutputParser(),
            () => new FileSystemMonitor())
    {
    }

    /// <summary>
    /// Factory method that constructs a <see cref="KiroCliOrchestrator"/> with default configuration
    /// and component implementations. Use this when you need a ready-to-use orchestrator without
    /// custom configuration. For testing or custom component injection, use the constructor directly.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <returns>A fully configured <see cref="KiroCliOrchestrator"/> with default settings.</returns>
    public static KiroCliOrchestrator Create(ILogger logger)
    {
        var config = new Configuration.Configuration();
        var callbackHandler = new CallbackHandler(logger);
        return new KiroCliOrchestrator(config, callbackHandler, logger);
    }

    public async Task<int> ExecutePromptAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken, Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        var processWrapper = _processWrapperFactory();
        using (processWrapper as IDisposable)
        {
            _activeProcess = processWrapper;
            var outputParser = _outputParserFactory();
            var fileSystemMonitor = _fileSystemMonitorFactory();

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
            catch (OperationCanceledException ex)
            {
                _logger.Information(ex, "Kiro CLI execution was cancelled");
                _callbackHandler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error, Message = "Execution was cancelled" });
                return KiroCliExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Kiro CLI execution failed");
                _callbackHandler.Invoke(KiroState.Error, new CallbackContext { State = KiroState.Error, Message = $"Execution failed: {ex.Message}" });
                return KiroCliExitCodes.GeneralFailure;
            }
            finally
            {
                _activeProcess = null;
            }
        }
    }

    /// <inheritdoc />
    public void Kill()
    {
        var p = _activeProcess;
        p?.Kill();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsExecuting)
        {
            Kill();
        }

        var p = _activeProcess;
        if (p is IDisposable disposable)
        {
            disposable.Dispose();
        }
        _activeProcess = null;

        GC.SuppressFinalize(this);
    }
}
