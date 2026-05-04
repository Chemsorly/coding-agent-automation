namespace KiroCliLib.Core;

/// <summary>
/// Defines the contract for managing the Kiro CLI process lifecycle.
/// </summary>
public interface IProcessWrapper : IDisposable
{
    /// <summary>Whether the process is currently running.</summary>
    bool IsRunning { get; }

    /// <summary>The exit code of the process, or null if it hasn't exited.</summary>
    int? ExitCode { get; }

    /// <summary>Timestamp of the last output line received from the process.</summary>
    DateTime LastOutputTime { get; }

    /// <summary>Occurs when a line is received on stdout.</summary>
    event EventHandler<string>? OutputReceived;

    /// <summary>Occurs when a line is received on stderr.</summary>
    event EventHandler<string>? ErrorReceived;

    /// <summary>
    /// Starts the Kiro CLI process with the given prompt and waits for it to exit.
    /// </summary>
    /// <param name="prompt">The prompt to send to the CLI.</param>
    /// <param name="workspaceDirectory">The workspace directory for the CLI process.</param>
    /// <param name="useResume">Whether to use the --resume flag for conversation continuity.</param>
    /// <param name="cancellationToken">Token to cancel the operation.</param>
    /// <returns>The process exit code.</returns>
    Task<int> StartAsync(string prompt, string workspaceDirectory, bool useResume, CancellationToken cancellationToken);

    /// <summary>
    /// Forcefully terminates the running process and its process tree.
    /// </summary>
    void Kill();
}
