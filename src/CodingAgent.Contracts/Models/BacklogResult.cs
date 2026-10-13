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
    bool IsTruncated)
{
    /// <summary>The issues that wait on still-open dependencies: the Attention screen's blocked issues.</summary>
    public IReadOnlyList<BlockedIssue> BlockedIssues() => Issues
        .Where(b => !b.IsReady && (b.BlockedBy.Count > 0 || (b.BlockedByUrls?.Count ?? 0) > 0))
        .Select(b => new BlockedIssue(b.Identifier, b.Title, b.BlockedBy, b.Url, b.BlockedByUrls))
        .ToList();
}
