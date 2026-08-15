using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Shared template resolution fallback: when a work item's AgentSelector is a subset of the template's
/// label set, resolves the matching profile to get the full MatchLabels, then retries template lookup.
/// Used by both <see cref="DispatchService"/> and <see cref="ConsolidationWorkItemDispatchService"/>.
/// </summary>
internal sealed class DispatchTemplateResolver
{
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly JobTemplateStore _templateProvider;

    public DispatchTemplateResolver(IAgentProfileStore? agentProfileStore, JobTemplateStore templateProvider)
    {
        _agentProfileStore = agentProfileStore;
        _templateProvider = templateProvider;
    }

    /// <summary>
    /// Fallback template resolution: when the work item's AgentSelector is a subset of the template's
    /// label set, resolve the matching profile to get the full MatchLabels, then retry template lookup.
    /// </summary>
    /// <param name="agentSelector">Comma-separated agent selector labels.</param>
    /// <param name="callerName">Name of the calling service (for log differentiation).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Resolved template and effective selector, or (null, null) if resolution fails.</returns>
    public async Task<(JobTemplate? Template, string? ResolvedSelector)> ResolveTemplateViaProfileAsync(
        string agentSelector, string callerName, CancellationToken ct)
    {
        if (_agentProfileStore is null)
            return (null, null);

        var selectorLabels = agentSelector
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (selectorLabels.Count == 0)
            return (null, null);

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);

        var profile = ProfileResolver.ResolveByRequiredLabels(profiles, selectorLabels);

        if (profile is null)
        {
            Log.Debug("{Caller}: no profile covers selector [{Selector}] for fallback template resolution",
                callerName, agentSelector);
            return (null, null);
        }

        // Use profile's MatchLabels as the template key (same as DispatchOrchestrationService.MapToRequest)
        var profileSelector = string.Join(",",
            profile.MatchLabels.OrderBy(l => l, StringComparer.Ordinal));

        var template = _templateProvider.Resolve(profileSelector);
        if (template is not null)
        {
            Log.Warning("{Caller}: AgentSelector [{Selector}] required profile expansion to resolve template. " +
                "Upstream code path may not be setting AgentSelector to full profile.MatchLabels. " +
                "Resolved via profile '{ProfileId}' → [{ProfileSelector}]",
                callerName, agentSelector, profile.Id, profileSelector);
        }

        return (template, profileSelector);
    }

    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchTemplateResolver>();
}
