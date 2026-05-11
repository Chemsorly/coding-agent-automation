namespace KiroCliLib.Core;

/// <summary>
/// Defines the contract for orchestrating Kiro CLI execution.
/// </summary>
public interface IKiroCliOrchestrator : IDisposable
{
    /// <summary>Whether an execution is currently in progress.</summary>
    bool IsExecuting { get; }

    /// <summary>OS process ID of the active agent process, if any.</summary>
    int? ActiveProcessId { get; }

    /// <summary>Whether the active agent process is still alive.</summary>
    bool? IsActiveProcessAlive { get; }

    /// <summary>Timestamp of the last output line received from the active process.</summary>
    DateTime? LastOutputTime { get; }

    Task<int> ExecutePromptAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null,
        string? resumeSessionId = null);

    /// <summary>
    /// Forcefully terminates the currently running agent process.
    /// No-op if no process is running.
    /// </summary>
    void Kill();
}
