using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Checks whether all dependency references in an issue body are satisfied (closed).
/// </summary>
public interface IDependencyChecker
{
    /// <summary>
    /// Checks dependencies for a single issue.
    /// </summary>
    /// <param name="issueIdentifier">The issue's own identifier (for self-reference filtering).</param>
    /// <param name="issueBody">The issue body text containing dependency references.</param>
    /// <param name="issueProvider">Provider to check referenced issue states.</param>
    /// <param name="stateCache">Shared cache for issue state lookups within a poll cycle.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DependencyCheckResult> CheckAsync(
        string issueIdentifier,
        string? issueBody,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct);
}
