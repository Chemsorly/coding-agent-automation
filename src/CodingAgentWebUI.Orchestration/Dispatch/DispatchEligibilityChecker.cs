using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using static CodingAgentWebUI.Orchestration.Dispatch.DispatchService;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Pure-logic eligibility checker for dispatch candidates. Evaluates whether a pending work item
/// can be dispatched based on: concurrency limits, template resolution (with profile fallback),
/// and PVC availability. Has no DB, SignalR, or K8s dependencies — all state is passed in.
/// Used by both <see cref="DispatchService"/> and <see cref="ConsolidationDispatchHandler"/>.
/// </summary>
internal sealed class DispatchEligibilityChecker
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchEligibilityChecker>();

    private readonly JobTemplateStore _templateProvider;
    private readonly IAgentProfileStore? _agentProfileStore;

    public DispatchEligibilityChecker(JobTemplateStore templateProvider, IAgentProfileStore? agentProfileStore)
    {
        ArgumentNullException.ThrowIfNull(templateProvider);
        _templateProvider = templateProvider;
        _agentProfileStore = agentProfileStore;
    }

    /// <summary>
    /// Evaluates dispatch eligibility for a single work item.
    /// Checks concurrency limit, resolves template (with profile fallback), re-checks concurrency
    /// for resolved selector, and validates PVC availability for kiro agents.
    /// </summary>
    /// <param name="item">The pending work item projection.</param>
    /// <param name="concurrencyBySelector">Current active counts per selector group (read-only for this method).</param>
    /// <param name="availablePvcCount">Number of PVCs currently available for kiro agents.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An <see cref="EligibilityResult"/> indicating whether the item can be dispatched.</returns>
    public async Task<EligibilityResult> CheckEligibilityAsync(
        PendingWorkItemProjection item,
        Dictionary<string, int> concurrencyBySelector,
        int availablePvcCount,
        CancellationToken ct)
    {
        // Check concurrency limit for the primary selector
        var maxConcurrent = _templateProvider.GetMaxConcurrent(item.AgentSelector);
        if (maxConcurrent > 0)
        {
            var current = concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0);
            if (current >= maxConcurrent)
            {
                Log.Debug("Selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                    item.AgentSelector, current, maxConcurrent, item.Id);
                return EligibilityResult.AtConcurrencyLimit();
            }
        }

        // Resolve template — fail immediately if no match (before PVC gating)
        var template = _templateProvider.Resolve(item.AgentSelector);
        var effectiveSelector = item.AgentSelector;

        if (template is null)
        {
            // Fallback: AgentSelector might be a subset of the template's label set.
            // Resolve profile to get the full label set, then retry template lookup.
            var (fallbackTemplate, resolvedSelector) = await ResolveTemplateViaProfileAsync(item.AgentSelector, ct);
            if (fallbackTemplate is null)
            {
                return EligibilityResult.NoTemplate($"No job template for selector: {item.AgentSelector}");
            }

            template = fallbackTemplate;
            effectiveSelector = resolvedSelector!;

            // Re-check concurrency limit against the resolved selector (the actual template key)
            var resolvedMaxConcurrent = template.MaxConcurrent;
            if (resolvedMaxConcurrent > 0)
            {
                var current = concurrencyBySelector.GetValueOrDefault(effectiveSelector, 0);
                if (current >= resolvedMaxConcurrent)
                {
                    Log.Debug("Resolved selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                        effectiveSelector, current, resolvedMaxConcurrent, item.Id);
                    return EligibilityResult.AtConcurrencyLimit();
                }
            }
        }

        var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

        if (isKiroAgent && availablePvcCount == 0)
        {
            Log.Information("No PVC available for kiro agent, skipping WorkItem {WorkItemId}", item.Id);
            return EligibilityResult.NoPvcAvailable();
        }

        return EligibilityResult.Eligible(template, effectiveSelector, isKiroAgent);
    }

    /// <summary>
    /// Fallback template resolution: when the work item's AgentSelector is a subset of the template's
    /// label set, resolve the matching profile to get the full MatchLabels, then retry template lookup.
    /// </summary>
    internal async Task<(JobTemplate? Template, string? ResolvedSelector)> ResolveTemplateViaProfileAsync(
        string agentSelector, CancellationToken ct)
    {
        if (_agentProfileStore is null)
            return (null, null);

        var selectorLabels = agentSelector
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (selectorLabels.Count == 0)
            return (null, null);

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);

        var resolver = new ProfileResolver();
        var profile = resolver.ResolveByRequiredLabels(profiles, selectorLabels);

        if (profile is null)
        {
            Log.Debug("No profile covers selector [{Selector}] for fallback template resolution",
                agentSelector);
            return (null, null);
        }

        // Use profile's MatchLabels as the template key (same as DispatchOrchestrationService.MapToRequest)
        var profileSelector = string.Join(",",
            profile.MatchLabels.OrderBy(l => l, StringComparer.Ordinal));

        var template = _templateProvider.Resolve(profileSelector);
        if (template is not null)
        {
            Log.Warning("AgentSelector [{Selector}] required profile expansion to resolve template. " +
                "Upstream code path may not be setting AgentSelector to full profile.MatchLabels. " +
                "Resolved via profile '{ProfileId}' → [{ProfileSelector}]",
                agentSelector, profile.Id, profileSelector);
        }

        return (template, profileSelector);
    }
}
