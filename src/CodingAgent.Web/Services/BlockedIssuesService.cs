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
    /// Labels that prevent an issue from being shown as "Ready" in the provider backlog and dispatch
    /// drawer. Uses OrdinalIgnoreCase because GitHub label names are case-insensitive in practice and
    /// the web layer normalises comparisons case-insensitively.
    /// <para>
    /// Includes all <see cref="AgentLabels.DispatchIneligibleLabels"/>, plus <see cref="AgentLabels.InProgress"/>
    /// (running issues must not show Ready — a second WorkItem is blocked by the DB unique index, not
    /// this label set), plus <see cref="AgentLabels.Next"/> (already queued for dispatch),
    /// plus the epic workflow labels (<see cref="AgentLabels.Epic"/>, <see cref="AgentLabels.EpicApproved"/>,
    /// <see cref="AgentLabels.EpicReview"/>), plus the non-agent <c>backlog</c> parking label.
    /// </para>
    /// </summary>
    internal static readonly IReadOnlySet<string> NotReadyLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // DispatchIneligibleLabels: Done, Error, NeedsRefinement, WontDo, Cancelled
        AgentLabels.Done,
        AgentLabels.Error,
        AgentLabels.NeedsRefinement,
        AgentLabels.WontDo,
        AgentLabels.Cancelled,
        // Active/in-flight states that are also not "Ready"
        AgentLabels.InProgress,
        AgentLabels.Next,
        // Epic workflow labels — active or awaiting human action
        AgentLabels.Epic,
        AgentLabels.EpicApproved,
        AgentLabels.EpicReview,
        // Non-agent parking label — not in AgentLabels by design (user/project convention)
        "backlog",
    };

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
            .Where(b => !b.IsReady && (b.BlockedBy.Count > 0 || (b.BlockedByUrls?.Count ?? 0) > 0))
            .Select(b => new BlockedIssue(b.Identifier, b.Title, b.BlockedBy, b.Url, b.BlockedByUrls))
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
            enabled = await GetEnabledTemplatesAsync(projectId, ct);

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
        // Dedupe by (tracker, number): issue numbers are unique only within a tracker, so #5 in two
        // trackers are two different issues and both belong in the backlog.
        var seen = new HashSet<(string ProviderId, string Identifier)>();
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
                if (await AddProviderBacklogAsync(providerId, provider, backlog, seen, ct))
                    isTruncated = true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "BlockedIssuesService: issue provider {ProviderId} failed; skipping", providerId);
            }
        }

        return new BacklogResult(backlog, isTruncated);
    }

    private async Task<List<PipelineJobTemplate>> GetEnabledTemplatesAsync(string? projectId, CancellationToken ct)
    {
        var templates = await _config.GetAllTemplatesAsync(ct);
        var enabled = templates.Where(t => t.Enabled).ToList();

        if (!string.IsNullOrEmpty(projectId))
        {
            var project = await _config.GetProjectByIdAsync(projectId, ct);
            var templateIds = project?.TemplateIds is { } ids
                ? new HashSet<string>(ids, StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);
            enabled = enabled.Where(t => templateIds.Contains(t.Id)).ToList();
        }

        return enabled;
    }

    /// <summary>
    /// Pages through one provider's open issues, appending each issue not yet <paramref name="seen"/> to
    /// <paramref name="backlog"/>. Returns true when fetching stopped at <see cref="MaxIssuesPerProvider"/>
    /// while more pages still existed.
    /// </summary>
    private async Task<bool> AddProviderBacklogAsync(
        string providerId,
        IIssueProvider provider,
        List<BacklogIssue> backlog,
        HashSet<(string ProviderId, string Identifier)> seen,
        CancellationToken ct)
    {
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
                if (!seen.Add((providerId, issue.Identifier)))
                    continue;
                // TODO: [WARNING] Cancellation is not checked between per-issue dependency
                // checks within a page. With PageSize=50 and a slow provider, a cancellation
                // request (e.g. component disposed) may not be honoured for up to 50× the
                // per-issue check latency. Consider adding ct.ThrowIfCancellationRequested()
                // at the top of this inner loop.
                backlog.Add(await ToBacklogIssueAsync(issue, provider, stateCache, ct));
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
                return pageResult.HasMore && providerFetched >= MaxIssuesPerProvider;
            }
            page++;
        }
    }

    private async Task<BacklogIssue> ToBacklogIssueAsync(
        IssueSummary issue, IIssueProvider provider, Dictionary<int, bool> stateCache, CancellationToken ct)
    {
        var check = await _dependencyChecker.CheckAsync(
            issue.Identifier, issue.Description ?? string.Empty, provider, stateCache, ct);
        // Override IsReady=false when the issue carries a lifecycle label that precludes
        // dispatch readiness, regardless of what the dependency checker returned.
        var isReady = check.IsReady
            && (issue.Labels is null || !issue.Labels.Any(l => NotReadyLabels.Contains(l)));
        return new BacklogIssue(issue.Identifier, issue.Title, issue.Url, isReady, check.BlockedBy, issue.Labels, issue.LabelColors, check.BlockedByUrls);
    }
}
