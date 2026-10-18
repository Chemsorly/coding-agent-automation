using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Ui;

namespace CodingAgent.Web.Services;

/// <summary>
/// The provider backlog of the Work screen and the "blocked issues" of the Attention screen. The API
/// computes both (spec 051 2.3b): open issues across the enabled templates' issue providers, each tagged
/// with its dispatch readiness. This is a live query — call it off the render path so it never blocks
/// the page. It degrades to an empty list when the API cannot be reached rather than throwing.
/// </summary>
public sealed class BlockedIssuesService(IUiBrowseClient browse, ILogger<BlockedIssuesService> logger)
{
    /// <summary>
    /// Returns open issues blocked by still-open dependencies, across the enabled templates' issue
    /// providers. When <paramref name="projectId"/> is set, only that project's templates are queried.
    /// </summary>
    public async Task<IReadOnlyList<BlockedIssue>> GetBlockedIssuesAsync(string? projectId, CancellationToken ct) =>
        (await GetBacklogAsync(projectId, ct)).BlockedIssues();

    /// <summary>
    /// Returns the open provider issues across the enabled templates' issue providers, each tagged with
    /// its dispatch readiness. <see cref="BacklogResult.IsTruncated"/> is set when a provider had more
    /// issues than the API fetches, so the UI can show an approximate count.
    /// </summary>
    public async Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct)
    {
        try
        {
            return await browse.GetBacklogAsync(projectId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "BlockedIssuesService: failed to load the backlog");
            return new BacklogResult([], IsTruncated: false);
        }
    }
}
