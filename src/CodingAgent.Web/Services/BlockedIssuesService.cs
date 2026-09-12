using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Web.Services;

/// <summary>
/// Computes the "blocked issues" shown on the Attention screen: open provider issues that cannot be
/// dispatched yet because they depend on issues that are still open. Reuses the same issue-provider +
/// dependency-checker path the dispatch drawer uses, aggregated across the enabled templates' providers.
/// This is a live query (no persisted data) — call it off the render path (async) so it never blocks the page.
/// </summary>
public sealed class BlockedIssuesService
{
    /// <summary>Issues fetched per page per provider. Providers enforce a maximum of 100.</summary>
    private const int PageSize = 50;

    /// <summary>
    /// Maximum issues fetched across all pages for a single provider. Caps dependency-checker cost:
    /// each issue triggers at least one IsIssueClosedAsync call per unique open dependency, so raising
    /// this limit proportionally increases live-query latency. 200 is a pragmatic balance.
    /// </summary>
    private const int MaxIssuesPerProvider = 200;

    private readonly IPipelineApiConfigClient _config;
    private readonly IProviderFactory _providerFactory;
    private readonly IDependencyChecker _dependencyChecker;

    public BlockedIssuesService(
        IPipelineApiConfigClient config,
        IProviderFactory providerFactory,
        IDependencyChecker dependencyChecker)
    {
        _config = config;
        _providerFactory = providerFactory;
        _dependencyChecker = dependencyChecker;
    }

    /// <summary>
    /// Returns open issues blocked by still-open dependencies, across the enabled templates' issue
    /// providers. When <paramref name="projectId"/> is set, only that project's templates are queried.
    /// Degrades to a partial/empty list on any provider error rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<BlockedIssue>> GetBlockedIssuesAsync(string? projectId, CancellationToken ct)
    {
        var backlog = await GetBacklogAsync(projectId, ct);
        return backlog.Issues
            .Where(b => !b.IsReady && b.BlockedBy.Count > 0)
            .Select(b => new BlockedIssue(b.Identifier, b.Title, b.BlockedBy, b.Url))
            .ToList();
    }

    /// <summary>
    /// Returns the open provider issues across the enabled templates' issue providers, each tagged with
    /// its dispatch readiness (ready, or blocked by still-open dependencies). Project-scoped and
    /// degrades to a partial/empty list on any provider error rather than throwing.
    /// When the number of issues exceeds <see cref="MaxIssuesPerProvider"/>, fetching stops and
    /// <see cref="BacklogResult.IsTruncated"/> is set so the UI can show an approximate count.
    /// </summary>
    public async Task<BacklogResult> GetBacklogAsync(string? projectId, CancellationToken ct)
    {
        List<PipelineJobTemplate> enabled;
        IReadOnlyList<ProviderConfig> issueConfigs;
        try
        {
            var templates = await _config.GetAllTemplatesAsync(ct);
            enabled = templates.Where(t => t.Enabled).ToList();

            if (!string.IsNullOrEmpty(projectId))
            {
                var project = await _config.GetProjectByIdAsync(projectId, ct);
                var templateIds = project?.TemplateIds is { } ids
                    ? new HashSet<string>(ids, StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal);
                enabled = enabled.Where(t => templateIds.Contains(t.Id)).ToList();
            }

            // Secrets are required so the created provider can authenticate against the issue API.
            issueConfigs = await _config.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, ct);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "BlockedIssuesService: failed to load templates/providers");
            return new BacklogResult([], IsTruncated: false);
        }

        var configById = issueConfigs.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var providerIds = enabled
            .Select(t => t.IssueProviderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var backlog = new List<BacklogIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // dedupe across providers
        // TODO: [WARNING] isTruncated is a single flag shared across all providers. If a provider
        // throws mid-pagination (exception on page 2+), the catch block skips setting isTruncated
        // even though that provider's backlog is incomplete — the UI will show an exact count with
        // no indication that results are partial. Consider setting isTruncated=true in the catch
        // path when providerFetched > 0 and the exception occurred mid-pagination (HasMore was true
        // on the last successful page).
        var isTruncated = false;

        foreach (var providerId in providerIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!configById.TryGetValue(providerId, out var cfg))
                continue;
            try
            {
                await using var provider = _providerFactory.CreateIssueProvider(cfg);

                // stateCache is at provider scope (outside the page loop) so IsIssueClosedAsync
                // responses are memoized across all pages — cross-page dependencies don't re-fetch.
                var stateCache = new Dictionary<int, bool>();
                var page = 1;
                var providerFetched = 0;

                while (true)
                {
                    var pageResult = await provider.ListOpenIssuesAsync(page, PageSize, ct);

                    foreach (var issue in pageResult.Items)
                    {
                        if (!seen.Add(issue.Identifier))
                            continue;
                        // TODO: [WARNING] Cancellation is not checked between per-issue dependency
                        // checks within a page. With PageSize=50 and a slow provider, a cancellation
                        // request (e.g. component disposed) may not be honoured for up to 50× the
                        // per-issue check latency. Consider adding ct.ThrowIfCancellationRequested()
                        // at the top of this inner loop.
                        var check = await _dependencyChecker.CheckAsync(
                            issue.Identifier, issue.Description ?? string.Empty, provider, stateCache, ct);
                        backlog.Add(new BacklogIssue(issue.Identifier, issue.Title, issue.Url, check.IsReady, check.BlockedBy, issue.Labels, issue.LabelColors));
                        providerFetched++;
                    }

                    if (!pageResult.HasMore || providerFetched >= MaxIssuesPerProvider)
                    {
                        // Truncated when we stopped because we hit the cap and more pages still exist.
                        // TODO: [WARNING] providerFetched can exceed MaxIssuesPerProvider by up to
                        // PageSize-1 (49 extra issues) because the cap check fires after processing
                        // all items in the current page, not before each item. The constant's doc
                        // comment says "200 is a pragmatic balance" but up to 249 can be fetched.
                        // Consider breaking the inner foreach early once providerFetched >= MaxIssuesPerProvider
                        // to enforce the cap precisely.
                        if (pageResult.HasMore && providerFetched >= MaxIssuesPerProvider)
                            isTruncated = true;
                        break;
                    }
                    page++;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "BlockedIssuesService: issue provider {ProviderId} failed; skipping", providerId);
            }
        }

        return new BacklogResult(backlog, isTruncated);
    }
}
