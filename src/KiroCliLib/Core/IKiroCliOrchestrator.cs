namespace KiroCliLib.Core;

/// <summary>
/// Defines the contract for orchestrating Kiro CLI execution.
/// </summary>
public interface IKiroCliOrchestrator
{
    Task<int> ExecutePromptAsync(
        string prompt,
        string workspaceDirectory,
        bool useResume,
        CancellationToken cancellationToken,
        Action<string>? onOutputLine = null);
}
