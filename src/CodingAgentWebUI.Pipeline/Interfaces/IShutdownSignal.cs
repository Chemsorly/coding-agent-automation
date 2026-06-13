namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Provides a cooperative shutdown signal that prevents new dispatches during graceful shutdown.
/// Set by <c>ShutdownService</c> before it cancels active runs, checked by
/// <c>JobQueueDrainService</c> and dispatch paths to avoid registering runs that would
/// immediately be cancelled.
/// </summary>
public interface IShutdownSignal
{
    /// <summary>
    /// Returns <c>true</c> once graceful shutdown has been initiated.
    /// Dispatch paths should check this before creating new runs.
    /// </summary>
    bool IsShuttingDown { get; }

    /// <summary>
    /// Signals that shutdown is in progress. Must be called before any active-run cancellation.
    /// </summary>
    void SignalShutdown();
}
