using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Computes the "blocked issues" shown on the Attention screen: open provider issues that cannot be
/// dispatched yet because they depend on issues that are still open. Reuses the same issue-provider +
/// dependency-checker path the dispatch drawer uses, aggregated across the enabled templates' providers.
/// This is a live query (no persisted data) — call it off the render path (async) so it never blocks the page.
/// </summary>
public sealed class BlockedIssuesService
{
    /// <summary>Open issues fetched per provider before dependency checking. Bounds the live query cost.</summary>
    private const int IssuesPerProvider = 20;

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
        return backlog
            .Where(b => !b.IsReady && b.BlockedBy.Count > 0)
            .Select(b => new BlockedIssue(b.Identifier, b.Title, b.BlockedBy, b.Url))
            .ToList();
    }

    /// <summary>
    /// Returns the open provider issues across the enabled templates' issue providers, each tagged with
    /// its dispatch readiness (ready, or blocked by still-open dependencies). Project-scoped and
    /// degrades to a partial/empty list on any provider error rather than throwing.
    /// </summary>
    public async Task<IReadOnlyList<BacklogIssue>> GetBacklogAsync(string? projectId, CancellationToken ct)
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
            return [];
        }

        var configById = issueConfigs.ToDictionary(c => c.Id, StringComparer.Ordinal);
        var providerIds = enabled
            .Select(t => t.IssueProviderId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var backlog = new List<BacklogIssue>();
        var seen = new HashSet<string>(StringComparer.Ordinal);   // dedupe across providers

        foreach (var providerId in providerIds)
        {
            ct.ThrowIfCancellationRequested();
            if (!configById.TryGetValue(providerId, out var cfg))
                continue;
            try
            {
                await using var provider = _providerFactory.CreateIssueProvider(cfg);
                var issues = await provider.ListOpenIssuesAsync(1, IssuesPerProvider, ct);

                // Shared across the provider's issues: memoizes "is issue #N closed?" per dependency check.
                var stateCache = new Dictionary<int, bool>();
                foreach (var issue in issues.Items)
                {
                    if (!seen.Add(issue.Identifier))
                        continue;
                    var result = await _dependencyChecker.CheckAsync(
                        issue.Identifier, issue.Description ?? string.Empty, provider, stateCache, ct);
                    backlog.Add(new BacklogIssue(issue.Identifier, issue.Title, issue.Url, result.IsReady, result.BlockedBy));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Warning(ex, "BlockedIssuesService: issue provider {ProviderId} failed; skipping", providerId);
            }
        }

        return backlog;
    }
}
