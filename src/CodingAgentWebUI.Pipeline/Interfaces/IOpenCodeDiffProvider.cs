using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Provides diff retrieval for OpenCode sessions. The pipeline resolves this
/// by casting the IAgentProvider when ProviderType == OpenCode.
/// Returns an empty list on failure (never throws).
/// </summary>
/// <remarks>
/// Resolution mechanism — the pipeline accesses this interface by casting the
/// <see cref="IAgentProvider"/> instance at the point where diffs are needed:
/// <code>
/// if (context.AgentProvider is IOpenCodeDiffProvider diffProvider)
/// {
///     var diffs = await diffProvider.GetSessionDiffAsync(ct);
/// }
/// else
/// {
///     // Fall back to IRepositoryProvider.GetFileChangesAsync (git-based)
///     var diffs = await context.RepoProvider.GetFileChangesAsync(workspacePath, ct);
/// }
/// </code>
/// This avoids adding OpenCode-specific methods to the shared <see cref="IAgentProvider"/>
/// interface while still allowing the pipeline to leverage the more accurate diff endpoint
/// when available.
/// </remarks>
public interface IOpenCodeDiffProvider
{
    /// <summary>
    /// Retrieves file changes for the current OpenCode session via the
    /// <c>GET /session/:id/diff</c> endpoint.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// A read-only list of file change summaries. Returns an empty list on any failure
    /// (HTTP error, timeout, network error) without throwing.
    /// </returns>
    Task<IReadOnlyList<FileChangeSummary>> GetSessionDiffAsync(CancellationToken ct);
}
