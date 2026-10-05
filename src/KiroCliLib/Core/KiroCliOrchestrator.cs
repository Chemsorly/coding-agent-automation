using KiroCliLib.Configuration;
using KiroCliLib.Models;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Orchestrates the complete Kiro CLI execution workflow.
/// </summary>
public class KiroCliOrchestrator : IKiroCliOrchestrator
{
    private readonly ILogger _logger;
    private readonly Func<IProcessWrapper> _processWrapperFactory;
    private readonly Func<IOutputParser> _outputParserFactory;
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
    /// <param name="logger">The logger instance.</param>
    /// <param name="processWrapperFactory">Factory to create <see cref="IProcessWrapper"/> instances.</param>
    /// <param name="outputParserFactory">Factory to create <see cref="IOutputParser"/> instances.</param>
    public KiroCliOrchestrator(
        ILogger logger,
        Func<IProcessWrapper> processWrapperFactory,
        Func<IOutputParser> outputParserFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(processWrapperFactory);
        ArgumentNullException.ThrowIfNull(outputParserFactory);
        _logger = logger;
        _processWrapperFactory = processWrapperFactory;
        _outputParserFactory = outputParserFactory;
    }

    /// <summary>
    /// Creates a new orchestrator with a custom process wrapper factory and default implementations for other components.
    /// </summary>
    public KiroCliOrchestrator(
        Configuration.Configuration config,
        ILogger logger,
        Func<IProcessWrapper> processWrapperFactory)
        : this(logger, processWrapperFactory, () => new OutputParser())
    {
    }

    /// <summary>
    /// Creates a new orchestrator with default concrete component implementations.
    /// </summary>
    public KiroCliOrchestrator(Configuration.Configuration config, ILogger logger)
        : this(
            logger,
            () => new ProcessWrapper(config, logger),
            () => new OutputParser())
    {
    }

    public async Task<int> ExecutePromptAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken, Func<string, Task>? onOutputLine = null, string? resumeSessionId = null, IReadOnlyDictionary<string, string>? environmentVariables = null)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        var processWrapper = _processWrapperFactory();
        using (processWrapper as IDisposable)
        {
            _activeProcess = processWrapper;
            var outputParser = _outputParserFactory();

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

                var exitCode = await processWrapper.StartAsync(prompt, workspaceDirectory, useResume, cancellationToken, resumeSessionId, environmentVariables);

                // Signal no more writes and wait for drain to complete
                channel?.Writer.TryComplete();
                if (drainTask != null)
                    await drainTask;

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
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Kills the active process, if any, and disposes it.
    /// </summary>
    /// <param name="disposing"><see langword="true"/> when called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (!disposing) return;

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
    }
}
