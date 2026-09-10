using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.Services;

/// <summary>
/// Builds and submits a <see cref="JobDistributionRequest"/> for a consolidation run.
/// Shared by the UI trigger path (<see cref="Components.Pages.Consolidation"/>) and
/// startup rehydration (<see cref="ConsolidationRehydrationExtensions"/>) so that both
/// paths produce identical <see cref="JobDistributionRequest"/> fields.
/// </summary>
internal sealed class ConsolidationDispatcher : IConsolidationDispatcher
{
    private static readonly ILogger Log = Serilog.Log.ForContext<ConsolidationDispatcher>();

    private readonly IWorkDistributor _workDistributor;
    private readonly IAgentProfileStore _profileStore;
    private readonly IConsolidationWorkspaceManager _workspaceManager;
    private readonly IPipelineConfigStore _configStore;

    public ConsolidationDispatcher(
        IWorkDistributor workDistributor,
        IAgentProfileStore profileStore,
        IConsolidationWorkspaceManager workspaceManager,
        IPipelineConfigStore configStore)
    {
        _workDistributor = workDistributor;
        _profileStore = profileStore;
        _workspaceManager = workspaceManager;
        _configStore = configStore;
    }

    /// <inheritdoc />
    public async Task DispatchRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        try
        {
            var liveConfig = await _configStore.LoadPipelineConfigAsync(ct);
            var profiles = await _profileStore.LoadAgentProfilesAsync(ct);

            // Resolve full profile MatchLabels from QueuedRequiredLabels to produce the
            // correct AgentSelector key (matches the startup rehydration path exactly).
            var requiredLabels = run.QueuedRequiredLabels ?? [];
            var profile = ProfileResolver.ResolveByRequiredLabels(profiles, requiredLabels.ToList());
            var selectorLabels = profile?.MatchLabels ?? requiredLabels;

            // If selectorLabels is still empty (QueuedRequiredLabels was null on an old persisted run,
            // or profiles loaded empty during a startup race), attempt a backward-compat fallback.
            // New runs will have QueuedRequiredLabels populated by ConsolidationService.BuildNewRun
            // so this block is only entered for runs created before that fix.
            if (selectorLabels.Count == 0)
            {
                if (!string.IsNullOrWhiteSpace(liveConfig.DefaultRequiredAgentLabels))
                {
                    var defaultLabels = liveConfig.DefaultRequiredAgentLabels
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList()
                        .AsReadOnly();
                    var defaultProfile = ProfileResolver.ResolveByRequiredLabels(profiles, defaultLabels);
                    selectorLabels = defaultProfile?.MatchLabels ?? defaultLabels;
                    Log.Warning(
                        "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels; fell back to DefaultRequiredAgentLabels → selector '{Selector}'",
                        run.RunId, AgentSelectorKey.From(selectorLabels));
                }
                else if (profiles.Count > 0)
                {
                    // Last resort: pick the highest-priority enabled profile.
                    // Only reached for old persisted runs (pre-fix) with no default labels configured.
                    var fallbackProfile = profiles
                        .Where(p => p.Enabled)
                        .OrderByDescending(p => p.Priority)
                        .ThenBy(p => p.Id, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (fallbackProfile is not null)
                    {
                        selectorLabels = fallbackProfile.MatchLabels;
                        Log.Warning(
                            "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels and no DefaultRequiredAgentLabels; fell back to profile '{ProfileId}' → selector '{Selector}'",
                            run.RunId, fallbackProfile.Id, AgentSelectorKey.From(selectorLabels));
                    }
                }
                else
                {
                    // Profiles are empty — startup race or profile store unavailable.
                    // Empty selector → dispatch will 409. Run stays Queued; rehydration retries on restart.
                    Log.Warning(
                        "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels and no profiles loaded (startup race?). Empty selector will cause 409; run stays Queued.",
                        run.RunId);
                }
            }

            var request = new JobDistributionRequest
            {
                IssueIdentifier = run.RunId,
                IssueProviderConfigId = ConsolidationConstants.ProviderConfigId,
                RepoProviderConfigId = "",
                InitiatedBy = ConsolidationConstants.InitiatedBy,
                TaskType = WorkItemTaskType.Consolidation,
                AgentSelector = AgentSelectorKey.From(selectorLabels),
                TimeoutSeconds = (int)liveConfig.AgentTimeout.TotalSeconds,
                ConsolidationRunType = run.Type,
                ConsolidationTemplateId = run.TemplateId,
                ConsolidationWorkspacePath = _workspaceManager.GetWorkspacePath(run.RunId),
                RunId = run.RunId,
                AutoDispatch = run.AutoDispatch,
                // Carry the traceparent stored at trigger time so the resulting WorkItem
                // inherits the original trace even when dispatched asynchronously.
                TraceContext = !string.IsNullOrEmpty(run.TraceParent)
                    ? new Dictionary<string, string> { ["traceparent"] = run.TraceParent }
                    : null
            };

            var result = await _workDistributor.DistributeAsync(request, ct);
            if (result.Success)
            {
                Log.Information(
                    "ConsolidationDispatcher: dispatched run {RunId} ({Type}) → WorkItem {WorkItemId}",
                    run.RunId, run.Type, result.WorkItemId);
            }
            else
            {
                // Leave the run as Queued — startup rehydration retries on next pod restart.
                Log.Warning(
                    "ConsolidationDispatcher: dispatch failed for run {RunId} ({Type}): {Error}. " +
                    "Run remains Queued; will retry on next orchestrator restart.",
                    run.RunId, run.Type, result.ErrorMessage);
            }
        }
        catch (Exception ex)
        {
            // Never let dispatch errors bubble to the UI — the run stays Queued and rehydration handles it.
            Log.Error(ex,
                "ConsolidationDispatcher: unexpected error dispatching run {RunId} ({Type}). " +
                "Run remains Queued; will retry on next orchestrator restart.",
                run.RunId, run.Type);
        }
    }
}
