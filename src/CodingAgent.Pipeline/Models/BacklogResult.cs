namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The result of a provider backlog fetch, carrying the fetched issues and a truncation flag.
/// </summary>
/// <param name="Issues">All fetched open issues, each tagged with dispatch readiness.</param>
/// <param name="IsTruncated">
/// True when the fetch loop stopped because it reached the per-provider cap and more pages
/// still existed. The UI should indicate "N+ open" rather than an exact count.
/// </param>
public sealed record BacklogResult(
    IReadOnlyList<BacklogIssue> Issues,
    bool IsTruncated);
