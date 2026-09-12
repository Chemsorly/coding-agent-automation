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
    private readonly IConsolidationService _consolidationService;

    public ConsolidationDispatcher(
        IWorkDistributor workDistributor,
        IAgentProfileStore profileStore,
        IConsolidationWorkspaceManager workspaceManager,
        IPipelineConfigStore configStore,
        IConsolidationService consolidationService)
    {
        _workDistributor = workDistributor;
        _profileStore = profileStore;
        _workspaceManager = workspaceManager;
        _configStore = configStore;
        _consolidationService = consolidationService;
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
            // ProfileResolver.ResolveByRequiredLabels returns null for an empty required-labels
            // list (instead of matching all profiles via Superset vacuous truth), so this path
            // correctly falls through to the fallback block below for runs with no baked labels.
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
                else
                {
                    // No QueuedRequiredLabels and no DefaultRequiredAgentLabels configured.
                    // An empty selector will cause the dispatch API to return 422 (no job template),
                    // which the permanent-failure branch below will detect and cascade the run to
                    // Failed (surfacing in Attention). This is a configuration error — an operator
                    // must set DefaultRequiredAgentLabels (or the run's QueuedRequiredLabels) and
                    // retrigger the consolidation run.
                    Log.Warning(
                        "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels and DefaultRequiredAgentLabels is not configured. " +
                        "Dispatch will return 422 (no job template for empty selector) — run will be cascaded to Failed.",
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
            else if (result.Permanent)
            {
                // Permanent failure: the agent selector has no matching job template and this
                // will never succeed without a configuration change. Cascade to Failed so the
                // run surfaces in the Attention view instead of staying stuck as Queued forever.
                Log.Error(
                    "ConsolidationDispatcher: permanent dispatch failure for run {RunId} ({Type}): {Error}. " +
                    "Cascading run to Failed — operator action required (configure a matching job template).",
                    run.RunId, run.Type, result.ErrorMessage);
                try
                {
                    // Use CancellationToken.None — not the caller-supplied ct — so this terminal-state
                    // write is not tied to the request lifetime. If ct is already cancelled (e.g. Blazor
                    // component teardown, host shutdown), passing ct would cause UpdateRunAsync to throw
                    // OperationCanceledException immediately, which the catch below silently swallows,
                    // leaving the run stuck as Queued — the exact failure mode this fix prevents.
                    // Matches the pattern in ConsolidationRehydrationExtensions (CancellationToken.None).
                    await _consolidationService.UpdateRunAsync(
                        new RunId(run.RunId),
                        ConsolidationRunStatus.Failed,
                        $"Dispatch failed permanently: {result.ErrorMessage}",
                        CancellationToken.None);
                }
                catch (Exception updateEx)
                {
                    Log.Error(updateEx,
                        "ConsolidationDispatcher: failed to cascade run {RunId} to Failed status after permanent dispatch failure",
                        run.RunId);
                }
            }
            else
            {
                // Transient failure (capacity, PVC unavailable): leave the run as Queued —
                // startup rehydration retries on next pod restart.
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
