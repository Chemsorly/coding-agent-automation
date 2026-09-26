using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Resolves the agent selector labels for a consolidation dispatch request.
/// Implements <see cref="IConsolidationSelectorResolver"/> using <see cref="IAgentProfileStore"/>
/// and <see cref="IPipelineConfigStore"/>, both of which are natural in the <c>CodingAgent.Web</c> layer.
/// This keeps infrastructure-layer dependencies out of <c>CodingAgent.Pipeline</c>.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Use <see cref="ProviderConfig.RequiredLabels"/> or <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/>
///         (via <see cref="LabelResolver"/>) to find required labels, then resolve to MatchLabels via the profile store.</item>
///   <item>Fall back to the live <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> from
///         <see cref="IPipelineConfigStore"/> if required-labels are empty.</item>
///   <item>Fall back to the first enabled profile's MatchLabels if no default is configured.</item>
///   <item>Return <c>null</c> when no profiles exist (startup race) — the caller must treat this as transient.</item>
/// </list>
/// </para>
/// </summary>
internal sealed class ConsolidationSelectorResolver : IConsolidationSelectorResolver
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ConsolidationSelectorResolver>();

    private readonly IAgentProfileStore _profileStore;
    private readonly IPipelineConfigStore _configStore;

    public ConsolidationSelectorResolver(
        IAgentProfileStore profileStore,
        IPipelineConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(configStore);
        _profileStore = profileStore;
        _configStore = configStore;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<string>?> ResolveAsync(
        ProviderConfig? repoConfig,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        var liveConfig = await _configStore.LoadPipelineConfigAsync(ct);
        var profiles = await _profileStore.LoadAgentProfilesAsync(ct);

        // TODO [WARNING]: Two different PipelineConfiguration sources are used in resolution:
        // `config` (the startup-time static snapshot passed by the caller) is consumed at step 1
        // via LabelResolver.ResolveRequiredLabels; `liveConfig` (loaded from the store on every call)
        // is consumed at step 2 via liveConfig.DefaultRequiredAgentLabels. If DefaultRequiredAgentLabels
        // is updated at runtime, step 1 reads the stale startup value while step 2 reads the updated
        // value. A repoConfig with empty RequiredLabels can silently route to the wrong agent selector
        // depending on which step fires. This dual-config design is inherited from ConsolidationDispatcher.
        // Document this asymmetry explicitly if intentional, or unify both steps to use liveConfig.
        // (review-findings-dotnetspecialist.md, review-findings-securityreviewer.md)
        return Resolve(repoConfig, config, liveConfig, profiles);
    }

    /// <summary>
    /// Synchronous resolution logic, separated for testability.
    /// </summary>
    internal static IReadOnlyList<string>? Resolve(
        ProviderConfig? repoConfig,
        PipelineConfiguration staticConfig,
        PipelineConfiguration liveConfig,
        IReadOnlyList<AgentProfile> profiles)
    {
        // 1. Try required labels from repo config or static DefaultRequiredAgentLabels.
        var requiredLabels = LabelResolver.ResolveRequiredLabels(repoConfig, staticConfig);
        if (requiredLabels is { Count: > 0 })
        {
            var profile = ProfileResolver.ResolveByRequiredLabels(profiles, requiredLabels);
            var selector = profile?.MatchLabels ?? requiredLabels;
            Log.Debug(
                "ConsolidationSelectorResolver: resolved selector '{Selector}' from required labels",
                AgentSelectorKey.From(selector));
            return selector;
        }

        // 2. Fall back to live DefaultRequiredAgentLabels from config store.
        if (!string.IsNullOrWhiteSpace(liveConfig.DefaultRequiredAgentLabels))
        {
            var defaultLabels = liveConfig.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
            var defaultProfile = ProfileResolver.ResolveByRequiredLabels(profiles, defaultLabels);
            var selector = defaultProfile?.MatchLabels ?? defaultLabels;
            Log.Warning(
                "ConsolidationSelectorResolver: no repo-scoped required labels; " +
                "fell back to live DefaultRequiredAgentLabels → selector '{Selector}'",
                AgentSelectorKey.From(selector));
            return selector;
        }

        // 3. No default labels configured. Pick the first enabled profile explicitly rather
        //    than passing empty labels to ResolveByRequiredLabels (which would match ALL profiles
        //    via Superset and return an arbitrary one).
        var firstEnabled = profiles
            .Where(p => p.Enabled)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();

        if (firstEnabled is not null)
        {
            Log.Warning(
                "ConsolidationSelectorResolver: no required labels and no DefaultRequiredAgentLabels; " +
                "fell back to highest-priority enabled profile '{Profile}' → selector '{Selector}'",
                firstEnabled.DisplayName, AgentSelectorKey.From(firstEnabled.MatchLabels));
            return firstEnabled.MatchLabels;
        }

        // 4. No profiles available at all (startup race).
        Log.Warning(
            "ConsolidationSelectorResolver: no agent profiles available (startup race?). " +
            "Returning null — caller must treat as transient failure.");
        return null;
    }
}
