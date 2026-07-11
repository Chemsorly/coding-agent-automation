using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared helper for resolving provider configs with cache-then-DB-fallback semantics.
/// Centralizes the pattern used by both <see cref="DispatchOrchestrationService"/> and
/// <see cref="AgentJobDispatcher"/> to avoid duplication.
/// </summary>
internal static class ProviderConfigResolver
{
    /// <summary>
    /// Resolves a provider config by ID from a pre-loaded list (cache), falling back to a direct
    /// DB query on miss. When <paramref name="required"/> is true, throws if config is not found.
    /// When the DB fallback succeeds, invalidates the stale list cache so subsequent lookups
    /// within the same request don't re-trigger fallback.
    /// </summary>
    /// <param name="store">Provider configuration store.</param>
    /// <param name="id">Provider config ID to resolve.</param>
    /// <param name="kind">Provider kind for the lookup.</param>
    /// <param name="cachedList">Pre-loaded list from <see cref="IProviderConfigStore.LoadProviderConfigsAsync"/>.</param>
    /// <param name="required">If true, throws <see cref="InvalidOperationException"/> when config not found.</param>
    /// <param name="logger">Serilog logger for diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved config, or null if not found and not required.</returns>
    public static async Task<ProviderConfig?> ResolveAsync(
        IProviderConfigStore store,
        string id,
        ProviderKind kind,
        IReadOnlyList<ProviderConfig> cachedList,
        bool required,
        ILogger logger,
        CancellationToken ct)
    {
        var config = cachedList.FirstOrDefault(c => c.Id == id);
        if (config is not null)
            return config;

        // Cache miss — fall back to direct DB query
        logger.Warning(
            "Provider config {ConfigId} ({Kind}) not found in cached list ({Count} items). Falling back to direct DB query.",
            id, kind, cachedList.Count);

        config = await store.GetProviderConfigByIdAsync(id, kind, ct);

        if (config is not null)
        {
            // Positive backfill: invalidate the stale list cache so subsequent lookups
            // in the same dispatch cycle will re-populate from DB with fresh data.
            // TODO: Cache invalidation is conditional on a runtime type check. If a non-composite IProviderConfigStore is passed, InvalidateCaches() is silently skipped. Consider logging when the cast fails or exposing InvalidateCaches on IProviderConfigStore directly.
            if (store is IConfigurationStore compositeStore)
                compositeStore.InvalidateCaches();
            return config;
        }

        if (required)
        {
            logger.Error(
                "Critical provider config {ConfigId} ({Kind}) not found in store after DB fallback. Cannot dispatch.",
                id, kind);
            throw new InvalidOperationException(
                $"Critical provider config '{id}' ({kind}) not found in store. Cannot dispatch.");
        }

        logger.Information(
            "Optional provider config {ConfigId} ({Kind}) not found after DB fallback. Skipping.",
            id, kind);
        return null;
    }
}
