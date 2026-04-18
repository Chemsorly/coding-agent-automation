using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

/// <summary>
/// Connects to an external CI/CD system and reads pipeline run status
/// for a given branch and commit.
/// </summary>
public interface IPipelineProvider
{
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
    /// </summary>
    Task<PipelineRunStatus> WaitForCompletionAsync(
        string branchName,
        string? commitSha,
        TimeSpan timeout,
        CancellationToken ct);
}
