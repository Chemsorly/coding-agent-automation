using KiroCliLib.Configuration;
using KiroCliLib.Models;
using Serilog;

namespace KiroCliLib.Core;

/// <summary>
/// Orchestrates the complete Kiro CLI execution workflow.
/// </summary>
public class KiroCliOrchestrator : IKiroCliOrchestrator
{
    /// <summary>
    /// What kiro-cli 2.29 prints to stderr (<c>error: ACP load_session failed</c>, exit 1) when
    /// <c>--resume-id</c> names a session its store cannot load.
    /// </summary>
    internal const string SessionLoadFailedError = "load_session failed";

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

        var channel = onOutputLine != null
            ? System.Threading.Channels.Channel.CreateUnbounded<string>(new System.Threading.Channels.UnboundedChannelOptions { SingleReader = true, SingleWriter = true })
            : null;

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

            var (exitCode, sessionNotLoaded) = await RunProcessAsync(
                prompt, workspaceDirectory, useResume, resumeSessionId, environmentVariables, channel?.Writer, cancellationToken);

            if (sessionNotLoaded)
            {
                // kiro-cli 2.29+ fails a --resume-id it cannot load (e.g. a session from another pod's
                // store); older versions started a fresh session, which is what the run gets now.
                _logger.Warning("Kiro session {SessionId} could not be loaded; running the prompt in a fresh session", resumeSessionId);
                channel?.Writer.TryWrite($"Kiro session {resumeSessionId} could not be loaded; continuing in a fresh session.");
                (exitCode, _) = await RunProcessAsync(
                    prompt, workspaceDirectory, useResume: false, resumeSessionId: null, environmentVariables, channel?.Writer, cancellationToken);
            }

            // Signal no more writes and wait for drain to complete
            channel?.Writer.TryComplete();
            if (drainTask != null)
                await drainTask;

            return exitCode;
        }
        catch (OperationCanceledException ex)
        {
            // Rethrown, not mapped to an exit code: callers tell a timeout from a cancellation by the
            // exception (TimeoutHelper), and ProcessWrapper has already killed the process.
            _logger.Information(ex, "Kiro CLI execution was cancelled");
            throw;
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

    /// <summary>
    /// Runs one kiro-cli process. <c>SessionNotLoaded</c> is true when it failed because the
    /// session named by <paramref name="resumeSessionId"/> could not be loaded.
    /// </summary>
    private async Task<(int ExitCode, bool SessionNotLoaded)> RunProcessAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        string? resumeSessionId,
        IReadOnlyDictionary<string, string>? environmentVariables,
        System.Threading.Channels.ChannelWriter<string>? output,
        CancellationToken cancellationToken)
    {
        var processWrapper = _processWrapperFactory();
        using (processWrapper as IDisposable)
        {
            _activeProcess = processWrapper;
            var outputParser = _outputParserFactory();
            var sessionLoadFailed = false;

            processWrapper.OutputReceived += (_, line) =>
            {
                _logger.Information("Kiro: {Line}", AnsiStripper.Strip(line));
                outputParser.ProcessLine(line);
                output?.TryWrite(line);
            };
            processWrapper.ErrorReceived += (_, line) =>
            {
                var clean = AnsiStripper.Strip(line);
                _logger.Debug("Kiro (stderr): {Line}", clean);
                outputParser.ProcessLine(line);
                if (clean.Contains(SessionLoadFailedError, StringComparison.OrdinalIgnoreCase))
                    sessionLoadFailed = true;
            };
            outputParser.StateChanged += (_, newState) =>
            {
                _logger.Debug("State changed to: {State}", newState);
            };

            var exitCode = await processWrapper.StartAsync(prompt, workspaceDirectory, useResume, cancellationToken, resumeSessionId, environmentVariables);
            return (exitCode, resumeSessionId is not null && exitCode != ExitCodes.Success && sessionLoadFailed);
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
