using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum AgentProviderType { KiroCli }

public interface IAgentProvider : IAsyncDisposable
{
    AgentProviderType ProviderType { get; }
    AgentHealthStatus GetHealthStatus();

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
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);
}
