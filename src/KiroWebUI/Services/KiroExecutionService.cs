using KiroCliLib.Configuration;
using KiroCliLib.Core;
using KiroCliLib.Models;
using KiroWebUI.Models;
using Serilog;
using System.Text.RegularExpressions;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Services;

/// <summary>
/// Scoped service that manages per-circuit session state and bridges
/// orchestrator events to the Blazor UI.
/// </summary>
public sealed class KiroExecutionService : IDisposable
{
    private readonly Configuration _config;
    private readonly ILogger _logger;
    private readonly Func<CallbackHandler, IKiroCliOrchestrator>? _orchestratorFactory;

    // State
    private readonly List<ChatMessage> _messages = new();
    private readonly object _messageLock = new();
    private bool _canResume;
    private bool _isExecuting;
    private bool _disposed;
    private readonly SemaphoreSlim _executionLock = new(1, 1);
    private readonly CancellationTokenSource _disposalCts = new();

    // UI update throttle
    private DateTime _lastNotifyTime = DateTime.MinValue;
    private bool _pendingNotify;
    private static readonly TimeSpan NotifyThrottle = TimeSpan.FromMilliseconds(100);

    // ANSI escape code stripper
    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07", RegexOptions.Compiled);

    // Events for UI binding
    public event Action? OnChange;
    public event Action<string>? OnOutputLineReceived;
    public event Action<KiroState>? OnStateChanged;

    // Public properties
    public IReadOnlyList<ChatMessage> Messages
    {
        get { lock (_messageLock) { return _messages.ToList().AsReadOnly(); } }
    }

    public bool IsExecuting => _isExecuting;
    public KiroState? CurrentState { get; private set; }

    public KiroExecutionService(Configuration config, ILogger logger)
        : this(config, logger, null)
    {
    }

    /// <summary>
    /// Constructor with optional orchestrator factory for testability.
    /// When factory is null, creates KiroCliOrchestrator directly (production path).
    /// </summary>
    public KiroExecutionService(Configuration config, ILogger logger, Func<CallbackHandler, IKiroCliOrchestrator>? orchestratorFactory)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);
        _config = config;
        _logger = logger;
        _orchestratorFactory = orchestratorFactory;
    }

    public async Task<ExecutionResult> ExecutePromptAsync(string prompt, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt cannot be empty.", nameof(prompt));

        if (!await _executionLock.WaitAsync(0, ct))
            throw new InvalidOperationException("An execution is already in progress.");

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposalCts.Token);
        var linkedToken = linkedCts.Token;

        try
        {
            _isExecuting = true;
            NotifyChanged();

            // Add user message
            var userMessage = new ChatMessage { Role = ChatMessageRole.User, Content = prompt };
            lock (_messageLock) { _messages.Add(userMessage); }

            // Create assistant message (streaming)
            var assistantMessage = new ChatMessage { Role = ChatMessageRole.Assistant, IsStreaming = true };
            lock (_messageLock) { _messages.Add(assistantMessage); }
            NotifyChanged();

            var outputLines = new List<string>();
            IReadOnlyList<FileChange>? fileChanges = null;
            TestResult? testResults = null;
            int? completionExitCode = null;

            // Create callback handler and orchestrator
            var callbackHandler = new CallbackHandler(_logger);

            // Register state-change callbacks for all states
            foreach (var state in Enum.GetValues<KiroState>())
            {
                callbackHandler.RegisterCallback(state, ctx =>
                {
                    if (_disposed) return;
                    CurrentState = ctx.State;
                    OnStateChanged?.Invoke(ctx.State);
                });
            }

            // Register completion callback to capture file changes and test results
            callbackHandler.RegisterOnCompleted(ctx =>
            {
                if (_disposed) return;
                fileChanges = ctx.FileChanges;
                testResults = ctx.TestResults;
                completionExitCode = ctx.ExitCode;
            });

            var orchestrator = _orchestratorFactory != null
                ? _orchestratorFactory(callbackHandler)
                : new KiroCliOrchestrator(_config, callbackHandler, _logger);

            var exitCode = await orchestrator.ExecutePromptAsync(
                prompt,
                _config.WorkspaceDirectory,
                _canResume,
                linkedToken,
                onOutputLine: line =>
                {
                    if (_disposed) return;
                    var cleanLine = AnsiRegex.Replace(line, string.Empty);
                    lock (_messageLock)
                    {
                        assistantMessage.Content += cleanLine + "\n";
                    }
                    outputLines.Add(cleanLine);
                    OnOutputLineReceived?.Invoke(cleanLine);
                    ThrottledNotify();
                });

            // Update resume flag based on exit code
            _canResume = exitCode == 0;

            // Finalize assistant message
            lock (_messageLock)
            {
                assistantMessage.IsStreaming = false;
                assistantMessage.ExitCode = exitCode;
                assistantMessage.FinalState = CurrentState;
                assistantMessage.FileChanges = fileChanges;
                assistantMessage.TestResults = testResults;
            }

            // Flush any pending throttled output
            NotifyChanged();

            return new ExecutionResult
            {
                ExitCode = exitCode,
                OutputLines = outputLines.AsReadOnly(),
                FileChanges = fileChanges,
                TestResults = testResults,
                FinalState = CurrentState ?? KiroState.Completed
            };
        }
        catch (TimeoutException ex)
        {
            _logger.Warning(ex, "Kiro CLI execution timed out");
            AddSystemMessage("Execution timed out.");
            lock (_messageLock)
            {
                var last = _messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant);
                if (last != null) { last.IsStreaming = false; last.FinalState = KiroState.Timeout; }
            }
            _canResume = false;
            NotifyChanged();
            return new ExecutionResult { ExitCode = 124, OutputLines = Array.Empty<string>(), FinalState = KiroState.Timeout };
        }
        catch (OperationCanceledException)
        {
            _logger.Information("Kiro CLI execution was cancelled");
            AddSystemMessage("Execution was cancelled.");
            lock (_messageLock)
            {
                var last = _messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant);
                if (last != null) { last.IsStreaming = false; last.FinalState = KiroState.Error; }
            }
            NotifyChanged();
            return new ExecutionResult { ExitCode = 130, OutputLines = Array.Empty<string>(), FinalState = KiroState.Error };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Kiro CLI execution failed unexpectedly");
            AddSystemMessage("An unexpected error occurred.");
            lock (_messageLock)
            {
                var last = _messages.LastOrDefault(m => m.Role == ChatMessageRole.Assistant);
                if (last != null) { last.IsStreaming = false; last.FinalState = KiroState.Error; }
            }
            _canResume = false;
            NotifyChanged();
            return new ExecutionResult { ExitCode = 1, OutputLines = Array.Empty<string>(), FinalState = KiroState.Error };
        }
        finally
        {
            _isExecuting = false;
            if (!_disposed)
            {
                try { _executionLock.Release(); } catch (ObjectDisposedException) { }
            }
            NotifyChanged();
        }
    }

    public void ClearSession()
    {
        lock (_messageLock)
        {
            _messages.Clear();
        }
        _canResume = false;
        CurrentState = null;
        NotifyChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _disposalCts.Cancel();
        _disposalCts.Dispose();
        _executionLock.Dispose();
        OnChange = null;
        OnOutputLineReceived = null;
        OnStateChanged = null;
    }

    private void AddSystemMessage(string content)
    {
        var msg = new ChatMessage { Role = ChatMessageRole.System, Content = content };
        lock (_messageLock) { _messages.Add(msg); }
    }

    private void NotifyChanged()
    {
        _lastNotifyTime = DateTime.UtcNow;
        _pendingNotify = false;
        OnChange?.Invoke();
    }

    private void ThrottledNotify()
    {
        var now = DateTime.UtcNow;
        if (now - _lastNotifyTime >= NotifyThrottle)
        {
            NotifyChanged();
        }
        else if (!_pendingNotify)
        {
            _pendingNotify = true;
            _ = Task.Delay(NotifyThrottle).ContinueWith(_ =>
            {
                if (_pendingNotify && !_disposed)
                {
                    NotifyChanged();
                }
            }, TaskScheduler.Default);
        }
    }
}
