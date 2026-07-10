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
    /// <param name="logger">The logger instance.</param>
    /// <param name="processWrapperFactory">Factory to create <see cref="IProcessWrapper"/> instances.</param>
    /// <param name="outputParserFactory">Factory to create <see cref="IOutputParser"/> instances.</param>
    /// <param name="fileSystemMonitorFactory">Factory to create <see cref="IFileSystemMonitor"/> instances.</param>
    public KiroCliOrchestrator(
        Configuration.Configuration config,
        ILogger logger,
        Func<IProcessWrapper> processWrapperFactory,
        Func<IOutputParser> outputParserFactory,
        Func<IFileSystemMonitor> fileSystemMonitorFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processWrapperFactory);
        ArgumentNullException.ThrowIfNull(outputParserFactory);
        ArgumentNullException.ThrowIfNull(fileSystemMonitorFactory);
        _config = config;
        _logger = logger;
        _processWrapperFactory = processWrapperFactory;
        _outputParserFactory = outputParserFactory;
        _fileSystemMonitorFactory = fileSystemMonitorFactory;
    }

    /// <summary>
    /// Creates a new orchestrator with a custom process wrapper factory and default implementations for other components.
    /// </summary>
    public KiroCliOrchestrator(
        Configuration.Configuration config,
        ILogger logger,
        Func<IProcessWrapper> processWrapperFactory)
        : this(config, logger, processWrapperFactory, () => new OutputParser(), () => new FileSystemMonitor())
    {
    }

    /// <summary>
    /// Creates a new orchestrator with default concrete component implementations.
    /// </summary>
    public KiroCliOrchestrator(Configuration.Configuration config, ILogger logger)
        : this(
            config,
            logger,
            () => new ProcessWrapper(config, logger),
            () => new OutputParser(),
            () => new FileSystemMonitor())
    {
    }

    public async Task<int> ExecutePromptAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken, Func<string, Task>? onOutputLine = null, string? resumeSessionId = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        var processWrapper = _processWrapperFactory();
        using (processWrapper as IDisposable)
        {
            _activeProcess = processWrapper;
            var outputParser = _outputParserFactory();
            var fileSystemMonitor = _fileSystemMonitorFactory();

            var channel = onOutputLine != null
                ? System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true })
                : null;

            processWrapper.OutputReceived += (_, line) =>
            {
                _logger.Information("Kiro: {Line}", AnsiStripper.Strip(line));
                outputParser.ProcessLine(line);
                channel?.Writer.TryWrite(line);
            };
            processWrapper.ErrorReceived += (_, line) => { _logger.Debug("Kiro (stderr): {Line}", AnsiStripper.Strip(line)); outputParser.ProcessLine(line); };
            outputParser.StateChanged += (_, newState) =>
            {
                _logger.Debug("State changed to: {State}", newState);
            };

            try
            {
                IReadOnlyList<FileSnapshot> beforeSnapshot;
                try { beforeSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to scan workspace before execution"); beforeSnapshot = Array.Empty<FileSnapshot>(); }

                // Start a background task to drain the channel and invoke the async callback
                Task? drainTask = null;
                if (channel != null && onOutputLine != null)
                {
                    drainTask = Task.Run(async () =>
                    {
                        await foreach (var line in channel.Reader.ReadAllAsync(cancellationToken))
                        {
                            await onOutputLine(line);
                        }
                    }, cancellationToken);
                }

                var exitCode = await processWrapper.StartAsync(prompt, workspaceDirectory, useResume, cancellationToken, resumeSessionId);

                // Signal no more writes and wait for drain to complete
                channel?.Writer.TryComplete();
                if (drainTask != null)
                    await drainTask;

                IReadOnlyList<FileChange> fileChanges;
                try
                {
                    var afterSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory);
                    fileChanges = fileSystemMonitor.CompareSnapshots(beforeSnapshot, afterSnapshot);
                }
                catch (Exception ex) { _logger.Warning(ex, "Failed to scan workspace after execution"); fileChanges = Array.Empty<FileChange>(); }

                return exitCode;
            }
            catch (OperationCanceledException ex)
            {
                _logger.Information(ex, "Kiro CLI execution was cancelled");
                return ExitCodes.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Kiro CLI execution failed");
                return ExitCodes.GeneralFailure;
            }
            finally
            {
                channel?.Writer.TryComplete();
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
