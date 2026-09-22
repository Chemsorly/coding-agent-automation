using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Shared helper for resolving provider configs with cache-then-DB-fallback semantics.
/// Centralizes the pattern used by both <see cref="DispatchOrchestrationService"/> and
/// <c>AgentJobDispatcher</c> to avoid duplication.
/// </summary>
internal static class ProviderConfigResolver
{
    /// <summary>
    /// Resolves a provider config by ID from a pre-loaded list (cache), falling back to a direct
    /// DB query on miss. When <paramref name="required"/> is true, throws if config is not found.
    /// When the DB fallback succeeds, invalidates the stale list cache so subsequent lookups
    /// within the same request don't re-trigger fallback.
    /// </summary>
    /// <param name="store">Configuration store for DB fallback queries and cache invalidation on backfill.</param>
    /// <param name="id">Provider config ID to resolve.</param>
    /// <param name="kind">Provider kind for the lookup.</param>
    /// <param name="cachedList">Pre-loaded list from <see cref="IProviderConfigStore.LoadProviderConfigsAsync"/>.</param>
    /// <param name="required">If true, throws <see cref="InvalidOperationException"/> when config not found.</param>
    /// <param name="logger">Serilog logger for diagnostics.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The resolved config, or null if not found and not required.</returns>
    public static async Task<ProviderConfig?> ResolveAsync(
        IConfigurationStore store,
        string id,
        ProviderKind kind,
        IReadOnlyList<ProviderConfig> cachedList,
        bool required,
        ILogger logger,
        CancellationToken ct)
    {
        var config = cachedList.TryGetProviderConfig(id);
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
            store.InvalidateCaches();
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

    /// <summary>
    /// Resolves a provider config by calling <paramref name="fetcher"/> (typically
    /// <c>IAgentHubFacade.GetProviderConfigByIdAsync</c>). Returns null on miss and logs a
    /// Warning. Use this overload at AgentGateway call sites that do not have access to a
    /// pre-loaded config list or <see cref="IConfigurationStore"/>.
    /// </summary>
    /// <param name="fetcher">Delegate that performs the actual config lookup.</param>
    /// <param name="id">Provider config ID — used only for the diagnostic log message.</param>
    /// <param name="kind">Provider kind — used only for the diagnostic log message.</param>
    /// <param name="logger">Serilog logger for diagnostics.</param>
    /// <returns>The resolved config, or null if not found.</returns>
    // TODO [WARNING]: Warning is always emitted when fetcher returns null, including after embedded
    // retry logic in the fetcher lambda. Callers that wrap custom retry logic should be aware that
    // the Warning fires after all retries are exhausted (the desired behavior), but any future
    // caller that intentionally returns null (e.g., feature-flag check) will also produce Warning
    // noise. Document this invariant at call sites that embed retry logic.
    // TODO [WARNING]: public modifier on a method of an internal static class is effectively
    // internal but inconsistent with the conventional style of marking internal class members
    // explicitly as internal. Normalise to internal to make access intent explicit and prevent
    // confusion if the class visibility is ever reviewed.
    public static async Task<ProviderConfig?> TryResolveAsync(
        Func<Task<ProviderConfig?>> fetcher,
        string id,
        ProviderKind kind,
        ILogger logger)
    {
        var config = await fetcher();
        if (config is null)
        {
            logger.Warning(
                "Provider config {ConfigId} ({Kind}) not found.",
                id, kind);
        }
        return config;
    }

    /// <summary>
    /// Resolves a provider config by calling <paramref name="fetcher"/> (typically
    /// <c>IAgentHubFacade.GetProviderConfigByIdAsync</c>). Throws
    /// <see cref="InvalidOperationException"/> on miss and logs an Error. Use this overload at
    /// AgentGateway call sites that do not have access to a pre-loaded config list or
    /// <see cref="IConfigurationStore"/>.
    /// </summary>
    /// <param name="fetcher">Delegate that performs the actual config lookup.</param>
    /// <param name="id">Provider config ID — used for the diagnostic log and exception message.</param>
    /// <param name="kind">Provider kind — used for the diagnostic log and exception message.</param>
    /// <param name="logger">Serilog logger for diagnostics.</param>
    /// <returns>The resolved config (never null).</returns>
    /// <exception cref="InvalidOperationException">Thrown when the config is not found.</exception>
    public static async Task<ProviderConfig> ResolveRequiredAsync(
        Func<Task<ProviderConfig?>> fetcher,
        string id,
        ProviderKind kind,
        ILogger logger)
    {
        var config = await fetcher();
        if (config is null)
        {
            logger.Error(
                "Provider config {ConfigId} ({Kind}) not found.",
                id, kind);
            throw new InvalidOperationException(
                $"Provider config '{id}' ({kind}) not found.");
        }
        return config;
    }
}
