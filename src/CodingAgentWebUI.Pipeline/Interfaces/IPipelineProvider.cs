using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum PipelineProviderType { GitHubActions, GitLabCI }

/// <summary>
/// Connects to an external CI/CD system and reads pipeline run status
/// for a given branch and commit.
/// </summary>
public interface IPipelineProvider : IAsyncDisposable
{
    /// <summary>
    /// Identifies the backing CI/CD system (e.g. GitHub Actions).
    /// </summary>
    PipelineProviderType ProviderType { get; }
    /// <summary>
    /// Gets the latest pipeline run status for a specific branch and commit.
    /// </summary>
    Task<PipelineRunStatus> GetRunStatusAsync(
        string branchName,
        string? commitSha,
        CancellationToken ct);

    /// <summary>
    /// Waits for all pipeline runs on a branch/commit to complete,
    /// polling at a configured interval until completion or timeout.
    /// When CI fails, implementations should populate
    /// <see cref="PipelineJobResult.LogContent"/> on failed jobs
    /// so callers receive actionable diagnostics without a second call.
    /// </summary>
    Task<PipelineRunStatus> WaitForCompletionAsync(
        string branchName,
        string? commitSha,
        TimeSpan timeout,
        CancellationToken ct);

    /// <summary>
    /// Fetches the full log content for a single CI job by its identifier.
    /// Returns <c>null</c> when logs are unavailable (API error, 404, timeout)
    /// rather than throwing.
    /// </summary>
    Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct);

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);
}
