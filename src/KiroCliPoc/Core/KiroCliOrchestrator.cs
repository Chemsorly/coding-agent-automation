using KiroCliPoc.Configuration;
using KiroCliPoc.Models;
using Serilog;

namespace KiroCliPoc.Core;

/// <summary>
/// Orchestrates the complete Kiro CLI execution workflow.
/// Coordinates ProcessWrapper, OutputParser, CallbackHandler, and FileSystemMonitor.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS, DOTNET_PRINCIPLES, SECURITY_INPUT_VALIDATION, PERFORMANCE_RESOURCE_DISPOSAL
/// </remarks>
public class KiroCliOrchestrator
{
    private readonly Configuration.Configuration _config;
    private readonly CallbackHandler _callbackHandler;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the KiroCliOrchestrator class.
    /// </summary>
    /// <param name="config">The configuration for Kiro CLI execution.</param>
    /// <param name="callbackHandler">The callback handler for state notifications.</param>
    /// <param name="logger">The logger for diagnostic output.</param>
    /// <exception cref="ArgumentNullException">Thrown when any parameter is null.</exception>
    public KiroCliOrchestrator(
        Configuration.Configuration config,
        CallbackHandler callbackHandler,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(callbackHandler);
        ArgumentNullException.ThrowIfNull(logger);

        _config = config;
        _callbackHandler = callbackHandler;
        _logger = logger;
    }

    /// <summary>
    /// Executes a single prompt with optional --resume flag for conversation history.
    /// </summary>
    /// <param name="prompt">The prompt to send to Kiro CLI.</param>
    /// <param name="workspaceDirectory">The workspace directory.</param>
    /// <param name="useResume">Whether to use --resume flag to continue previous conversation.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The exit code from the Kiro CLI process.</returns>
    /// <exception cref="ArgumentNullException">Thrown when prompt or workspaceDirectory is null.</exception>
    public async Task<int> ExecutePromptAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prompt);
        ArgumentNullException.ThrowIfNull(workspaceDirectory);

        _logger.Debug("Executing prompt with {ResumeFlag}", useResume ? "--resume" : "new session");

        // Initialize components
        using var processWrapper = new ProcessWrapper(_config, _logger);
        var outputParser = new OutputParser();
        var fileSystemMonitor = new FileSystemMonitor();

        // Wire up events
        processWrapper.OutputReceived += (sender, line) =>
        {
            _logger.Information("Kiro: {Line}", line);
            outputParser.ProcessLine(line);
        };

        processWrapper.ErrorReceived += (sender, line) =>
        {
            _logger.Debug("Kiro (stderr): {Line}", line);
            outputParser.ProcessLine(line);
        };

        outputParser.StateChanged += (sender, newState) =>
        {
            _logger.Debug("State changed to: {State}", newState);
            
            var callbackContext = new CallbackContext
            {
                State = newState,
                Message = null,
                Files = null,
                TestResults = outputParser.TestResults,
                ExitCode = null
            };

            _callbackHandler.Invoke(newState, callbackContext);
        };

        outputParser.ProgressUpdate += (sender, message) =>
        {
            _logger.Debug("Progress: {Message}", message);
        };

        outputParser.FileDetected += (sender, fileChange) =>
        {
            _logger.Debug("File detected: {Type} - {Path}", fileChange.Type, fileChange.Path);
        };

        outputParser.TestResultDetected += (sender, testResult) =>
        {
            _logger.Debug("Test results: {Passed}/{Total} passed, Coverage: {Coverage}%",
                testResult.PassedTests, testResult.TotalTests, testResult.Coverage);
        };

        try
        {
            // Scan workspace before execution
            _logger.Debug("Scanning workspace before execution: {WorkspaceDirectory}", workspaceDirectory);
            
            IReadOnlyList<FileSnapshot> beforeSnapshot;
            try
            {
                beforeSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory);
                _logger.Debug("Workspace scan complete: {FileCount} files found", beforeSnapshot.Count);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to scan workspace before execution, continuing without file monitoring");
                beforeSnapshot = Array.Empty<FileSnapshot>();
            }

            // Execute prompt with optional --resume flag
            _logger.Debug("Starting Kiro CLI process");
            var exitCode = await processWrapper.StartAsync(prompt, workspaceDirectory, useResume, cancellationToken);
            _logger.Debug("Kiro CLI process completed with exit code: {ExitCode}", exitCode);

            // Scan workspace after execution
            _logger.Debug("Scanning workspace after execution");
            IReadOnlyList<FileSnapshot> afterSnapshot;
            IReadOnlyList<FileChange> fileChanges;
            
            try
            {
                afterSnapshot = fileSystemMonitor.ScanWorkspace(workspaceDirectory);
                fileChanges = fileSystemMonitor.CompareSnapshots(beforeSnapshot, afterSnapshot);
                _logger.Debug("Workspace scan complete: {ChangeCount} changes detected", fileChanges.Count);

            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to scan workspace after execution, skipping file change detection");
                fileChanges = Array.Empty<FileChange>();
            }

            // Invoke OnCompleted callback with file changes
            _callbackHandler.Invoke(KiroState.Completed, new CallbackContext
            {
                State = KiroState.Completed,
                Message = fileChanges.Count > 0 ? $"{fileChanges.Count} file(s) changed" : "Execution completed successfully",
                Files = fileChanges.Select(fc => fc.Path).ToList(),
                TestResults = outputParser.TestResults,
                ExitCode = exitCode
            });

            return exitCode;
        }
        catch (TimeoutException ex)
        {
            _logger.Error(ex, "Kiro CLI execution timed out");
            
            _callbackHandler.Invoke(KiroState.Timeout, new CallbackContext
            {
                State = KiroState.Timeout,
                Message = $"Execution timed out after {_config.Timeout}",
                Files = null,
                TestResults = null,
                ExitCode = null
            });

            return 124; // Standard timeout exit code
        }
        catch (OperationCanceledException ex)
        {
            _logger.Information(ex, "Kiro CLI execution was cancelled");
            
            _callbackHandler.Invoke(KiroState.Error, new CallbackContext
            {
                State = KiroState.Error,
                Message = "Execution was cancelled by user",
                Files = null,
                TestResults = null,
                ExitCode = null
            });

            return 130; // Standard cancellation exit code (128 + SIGINT)
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Kiro CLI execution failed with unexpected error");
            
            _callbackHandler.Invoke(KiroState.Error, new CallbackContext
            {
                State = KiroState.Error,
                Message = $"Execution failed: {ex.Message}",
                Files = null,
                TestResults = null,
                ExitCode = null
            });

            return 1; // Generic error exit code
        }
    }
}
