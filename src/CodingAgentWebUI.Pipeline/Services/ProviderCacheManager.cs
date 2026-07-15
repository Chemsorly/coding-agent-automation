using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Owns the issue and repository provider caches, reconciling them with the needed set
/// of provider IDs each cycle. Disposes evicted providers and creates missing ones via
/// the <see cref="IProviderFactory"/>.
/// </summary>
// TODO: Add direct unit tests for ProviderCacheManager. Currently tested only indirectly
// through integration-level PipelineLoopServiceTests. Direct tests should cover: reconciliation
// lifecycle, eviction on auth error, double-dispose safety, and factory-throw scenarios.
internal sealed class ProviderCacheManager : IAsyncDisposable
{
    private readonly IProviderFactory _providerFactory;
    private readonly Serilog.ILogger _logger;

    /// <summary>Provider cache keyed by IssueProviderId. Reused across cycles.</summary>
    // TODO: Mutable dictionaries exposed via internal properties. Both TemplatePoller and
    // DispatchScheduler read directly while ReconcileCacheAsync mutates them. Safe today
    // (single-threaded loop) but fragile if concurrency is introduced. Consider exposing
    // IReadOnlyDictionary<> or TryGetProvider() accessors instead.
    internal Dictionary<string, IIssueProvider> IssueProviders { get; } = new();

    /// <summary>Repository provider cache keyed by RepoProviderId. Reused across cycles.</summary>
    internal Dictionary<string, IRepositoryProvider> RepoProviders { get; } = new();

    internal ProviderCacheManager(IProviderFactory providerFactory, Serilog.ILogger logger)
    {
        _providerFactory = providerFactory;
        _logger = logger;
    }

    /// <summary>
    /// Reconciles the issue provider cache with the needed set of IssueProviderIds.
    /// </summary>
    internal Task ReconcileIssueProvidersAsync(
        HashSet<string> neededIssueProviderIds,
        IReadOnlyList<ProviderConfig> issueProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(IssueProviders, neededIssueProviderIds, issueProviderConfigs,
            _providerFactory.CreateIssueProvider, "issue", ct);

    /// <summary>
    /// Reconciles the repository provider cache with the needed set of RepoProviderIds.
    /// </summary>
    internal Task ReconcileRepoProvidersAsync(
        HashSet<string> neededRepoProviderIds,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        CancellationToken ct)
        => ReconcileCacheAsync(RepoProviders, neededRepoProviderIds, repoProviderConfigs,
            _providerFactory.CreateRepositoryProvider, "repo", ct);

    /// <summary>
    /// Evicts a provider from the cache due to an auth error. Disposes and removes it
    /// so the next cycle recreates a fresh instance.
    /// </summary>
    internal async Task EvictOnAuthErrorAsync(string issueProviderId)
    {
        if (IssueProviders.TryGetValue(issueProviderId, out var provider))
        {
            try { await provider.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose provider {ProviderId} after auth error", issueProviderId); }
            IssueProviders.Remove(issueProviderId);
        }
    }

    /// <summary>
    /// Disposes all cached providers (both issue and repo).
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var kvp in IssueProviders)
        {
            try { await kvp.Value.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached provider {ProviderId}", kvp.Key); }
        }
        IssueProviders.Clear();

        foreach (var kvp in RepoProviders)
        {
            try { await kvp.Value.DisposeAsync(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached repo provider {ProviderId}", kvp.Key); }
        }
        RepoProviders.Clear();
    }

    /// <summary>
    /// Generic cache reconciliation: evicts stale entries (disposes before removing),
    /// creates missing entries via the factory delegate.
    /// </summary>
    private async Task ReconcileCacheAsync<TProvider>(
        Dictionary<string, TProvider> cache,
        HashSet<string> neededIds,
        IReadOnlyList<ProviderConfig> providerConfigs,
        Func<ProviderConfig, TProvider> factory,
        string providerKindLabel,
        CancellationToken ct) where TProvider : IAsyncDisposable
    {
        // Evict stale entries
        var staleKeys = cache.Keys.Where(k => !neededIds.Contains(k)).ToList();
        foreach (var key in staleKeys)
        {
            if (cache.TryGetValue(key, out var provider))
            {
                try { await provider.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose cached {Kind} provider {ProviderId}", providerKindLabel, key); }
                cache.Remove(key);
            }
        }

        // Create missing entries
        foreach (var neededId in neededIds)
        {
            ct.ThrowIfCancellationRequested();

            if (!cache.ContainsKey(neededId))
            {
                var config = providerConfigs.FirstOrDefault(c => c.Id == neededId);
                if (config is not null)
                {
                    try
                    {
                        cache[neededId] = factory(config);
                    }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Failed to create {Kind} provider for {ProviderId}", providerKindLabel, neededId);
                    }
                }
            }
        }
    }
}
