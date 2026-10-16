using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Resolves the agent selector for a consolidation dispatch request.
/// Implements <see cref="IConsolidationSelectorResolver"/> using <see cref="IAgentProfileStore"/>
/// and <see cref="IPipelineConfigStore"/>. It lives here rather than in <c>CodingAgent.Pipeline</c>
/// so the stores stay out of that layer. The Pipeline API, which triggers consolidation since spec 051
/// 2.4, registers it.
/// <para>
/// Resolution order:
/// <list type="number">
///   <item>Use <see cref="ProviderConfig.RequiredLabels"/> or <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/>
///         (via <see cref="LabelResolver"/>) to find required labels, then resolve to a profile via the profile store.</item>
///   <item>Fall back to the live <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> from
///         <see cref="IPipelineConfigStore"/> if required-labels are empty.</item>
///   <item>Fall back to the first enabled profile that has a registered job template. If
///         <paramref name="jobTemplateStore"/> is null (e.g. non-Kubernetes host), template-filtering
///         is skipped and the highest-priority enabled profile is returned as before.</item>
///   <item>Return <c>null</c> when no profiles exist (startup race) — the caller must treat this as transient.</item>
/// </list>
/// </para>
/// </summary>
public sealed class ConsolidationSelectorResolver : IConsolidationSelectorResolver
{
    private static readonly Serilog.ILogger Log = Serilog.Log.ForContext<ConsolidationSelectorResolver>();

    private readonly IAgentProfileStore _profileStore;
    private readonly IPipelineConfigStore _configStore;
    private readonly JobTemplateStore? _jobTemplateStore;

    public ConsolidationSelectorResolver(
        IAgentProfileStore profileStore,
        IPipelineConfigStore configStore,
        JobTemplateStore? jobTemplateStore = null)
    {
        ArgumentNullException.ThrowIfNull(profileStore);
        ArgumentNullException.ThrowIfNull(configStore);
        _profileStore = profileStore;
        _configStore = configStore;
        _jobTemplateStore = jobTemplateStore;
    }

    /// <inheritdoc />
    public async Task<ConsolidationAgentSelection?> ResolveAsync(
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
        // depending on which step fires. This dual-config design was inherited from the former ConsolidationDispatcher.
        // Document this asymmetry explicitly if intentional, or unify both steps to use liveConfig.
        // (review-findings-dotnetspecialist.md, review-findings-securityreviewer.md)

        // Build the set of selectors that have a registered job template so step 3 can filter.
        // When _jobTemplateStore is null (non-Kubernetes host or Scheduler) the set is null and
        // step 3 behaves as before (no filtering).
        IReadOnlyCollection<string>? dispatchableSelectors = _jobTemplateStore is not null
            ? _jobTemplateStore.GetAllTemplates()
                .Select(t => JobTemplateStore.NormalizeLabels(t.Labels))
                .ToHashSet(StringComparer.Ordinal)
            : null;

        return Resolve(repoConfig, config, liveConfig, profiles, dispatchableSelectors);
    }

    /// <summary>
    /// Synchronous resolution logic, separated for testability.
    /// </summary>
    /// <param name="repoConfig">Repo-scoped provider config (may be null for global runs).</param>
    /// <param name="staticConfig">Startup-time pipeline configuration snapshot.</param>
    /// <param name="liveConfig">Live pipeline configuration from the config store.</param>
    /// <param name="profiles">All agent profiles (enabled and disabled).</param>
    /// <param name="dispatchableSelectors">
    /// Pre-computed set of normalised agent-selector keys that have a job template in the store.
    /// When <c>null</c>, step 3 skips template filtering and returns the highest-priority enabled
    /// profile (legacy behaviour, backward-compatible with hosts that do not use Kubernetes).
    /// </param>
    internal static ConsolidationAgentSelection? Resolve(
        ProviderConfig? repoConfig,
        PipelineConfiguration staticConfig,
        PipelineConfiguration liveConfig,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyCollection<string>? dispatchableSelectors = null)
    {
        // 1. Try required labels from repo config or static DefaultRequiredAgentLabels.
        var requiredLabels = LabelResolver.ResolveRequiredLabels(repoConfig, staticConfig);
        if (requiredLabels is { Count: > 0 })
        {
            var profile = ProfileResolver.ResolveByRequiredLabels(profiles, requiredLabels);
            if (profile is not null)
            {
                Log.Debug(
                    "ConsolidationSelectorResolver: resolved selector '{Selector}' via profile '{Profile}' from required labels",
                    profile.JobTemplateKey(), profile.DisplayName);
                return new ConsolidationAgentSelection(profile.JobTemplateKey(), profile.Id);
            }

            // Labels known but no profile matched — use labels as selector.
            var fallbackSelector = AgentSelectorKey.From(requiredLabels);
            Log.Debug(
                "ConsolidationSelectorResolver: resolved selector '{Selector}' from required labels (no profile match)",
                fallbackSelector);
            return new ConsolidationAgentSelection(fallbackSelector, null);
        }

        // 2. Fall back to live DefaultRequiredAgentLabels from config store.
        if (!string.IsNullOrWhiteSpace(liveConfig.DefaultRequiredAgentLabels))
        {
            var defaultLabels = liveConfig.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
            var defaultProfile = ProfileResolver.ResolveByRequiredLabels(profiles, defaultLabels);

            if (defaultProfile is not null)
            {
                Log.Warning(
                    "ConsolidationSelectorResolver: no repo-scoped required labels; " +
                    "fell back to live DefaultRequiredAgentLabels → profile '{Profile}' selector '{Selector}'",
                    defaultProfile.DisplayName, defaultProfile.JobTemplateKey());
                return new ConsolidationAgentSelection(defaultProfile.JobTemplateKey(), defaultProfile.Id);
            }

            var fallbackSelector = AgentSelectorKey.From(defaultLabels);
            Log.Warning(
                "ConsolidationSelectorResolver: no repo-scoped required labels; " +
                "fell back to live DefaultRequiredAgentLabels → selector '{Selector}' (no profile match)",
                fallbackSelector);
            return new ConsolidationAgentSelection(fallbackSelector, null);
        }

        // 3. No default labels configured. Pick the highest-priority enabled profile that has a
        //    registered job template. When dispatchableSelectors is null (non-Kubernetes host),
        //    skip the template filter so existing behaviour is preserved.
        var enabledProfiles = profiles
            .Where(p => p.Enabled)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();

        if (dispatchableSelectors is not null)
        {
            var withTemplate = enabledProfiles
                .Where(p => dispatchableSelectors.Contains(p.JobTemplateKey()))
                .ToList();

            if (withTemplate.Count == 0 && enabledProfiles.Count > 0)
            {
                var names = string.Join(", ", enabledProfiles.Select(p => $"'{p.DisplayName}'"));
                Log.Warning(
                    "ConsolidationSelectorResolver: no enabled profiles have a matching job template; " +
                    "enabled profiles: [{Profiles}]; the job-templates ConfigMap has no entry for them — " +
                    "global consolidation trigger rejected",
                    names);
                return null;
            }

            enabledProfiles = withTemplate;
        }

        var firstEnabled = enabledProfiles.FirstOrDefault();

        if (firstEnabled is not null)
        {
            Log.Warning(
                "ConsolidationSelectorResolver: no required labels and no DefaultRequiredAgentLabels; " +
                "fell back to highest-priority enabled profile '{Profile}' → selector '{Selector}'",
                firstEnabled.DisplayName, firstEnabled.JobTemplateKey());
            return new ConsolidationAgentSelection(firstEnabled.JobTemplateKey(), firstEnabled.Id);
        }

        // 4. No profiles available at all (startup race).
        Log.Warning(
            "ConsolidationSelectorResolver: no agent profiles available (startup race?). " +
            "Returning null — caller must treat as transient failure.");
        return null;
    }
}
