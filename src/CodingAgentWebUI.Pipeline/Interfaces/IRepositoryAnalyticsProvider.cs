namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Repository analytics — commit count queries used by staleness detection.
/// Consumed by <c>DispatchInfrastructure</c> to determine whether the codebase has
/// evolved significantly since the last analysis.
/// </summary>
public interface IRepositoryAnalyticsProvider : IAsyncDisposable
{
    /// <summary>
    /// Returns the count of commits on the default branch since the given timestamp.
    /// Used by analysis staleness detection to determine if the codebase has evolved
    /// significantly since the last analysis. Paginates all results since the given
    /// timestamp. Callers bound usage via AnalysisCommitThreshold configuration (max 1000).
    /// Default returns 0 (effectively disabling commit-count staleness detection).
    /// </summary>
    Task<int> GetCommitCountSinceAsync(DateTimeOffset since, CancellationToken ct)
        => Task.FromResult(0);
}
