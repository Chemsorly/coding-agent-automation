using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum AgentProviderType { KiroCli, OpenCode }

public interface IAgentProvider : IAsyncDisposable
{
    AgentProviderType ProviderType { get; }
    AgentHealthStatus GetHealthStatus();

    /// <summary>
    /// Whether this provider supports concurrent <see cref="ExecuteAsync"/> calls.
    /// When true, the review step can run multiple agents in parallel via Task.WhenAll.
    /// Providers with process-level session constraints (e.g., Kiro CLI's SQLite DB)
    /// return false and fall back to sequential execution.
    /// </summary>
    bool SupportsParallelExecution { get; }

    /// <summary>
    /// Provider-specific paths that should be excluded from commits when steering files are written.
    /// Merged with config.BlacklistedPaths at commit time.
    /// </summary>
    IReadOnlyList<string> SteeringBlacklistPaths { get; }

    /// <summary>
    /// Ensures a CLI session is established for the given workspace path by sending a
    /// read-only warm-up prompt on the first call. Subsequent calls for the same
    /// (normalized) workspace path are no-ops. Exceptions from the underlying agent
    /// execution are caught, logged as warnings, and not rethrown; the session is not
    /// marked as established on failure so the next call will retry.
    /// </summary>
    Task EnsureSessionAsync(string workspacePath, CancellationToken ct);

    Task<AgentResult> ExecuteAsync(AgentRequest request, CancellationToken ct, Action<string>? onOutputLine = null);

    /// <summary>
    /// Forcefully terminates the currently running agent process.
    /// Called by the stall monitor when the agent is unresponsive.
    /// No-op if no process is running.
    /// </summary>
    Task KillAsync();

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);

    /// <summary>
    /// Retrieves the most recent session ID for the given workspace.
    /// Returns null if no sessions exist or the operation is not supported.
    /// </summary>
    Task<string?> GetLatestSessionIdAsync(string workspacePath, CancellationToken ct);
}
