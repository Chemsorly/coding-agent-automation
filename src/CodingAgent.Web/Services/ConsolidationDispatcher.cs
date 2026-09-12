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

            // Resolve the AgentSelector key for this run.
            //
            // Two distinct paths:
            //
            // NEW RUN PATH (QueuedRequiredLabels is set):
            //   The required labels were baked at trigger time by ConsolidationService.BuildNewRun.
            //   Use them directly for profile resolution — this is the normal, deterministic path.
            //
            // LEGACY RUN PATH (QueuedRequiredLabels is null):
            //   The run was created before the label-baking fix, or DefaultRequiredAgentLabels was
            //   not configured at trigger time. The required labels are unknown. We MUST NOT fall
            //   through to ProfileResolver.ResolveByRequiredLabels with an empty required set because
            //   Superset-matching with an empty target set matches EVERY profile and deterministically
            //   picks the Id-lexicographically-smallest enabled profile — which is almost certainly
            //   wrong (e.g., "kiro,python,python312" when only "kiro,dotnet,dotnet10" has a template).
            //   Instead, use the live DefaultRequiredAgentLabels as a fallback, or produce an empty
            //   selector (warning path) if no default is configured.
            IReadOnlyList<string> selectorLabels;
            if (run.QueuedRequiredLabels is { Count: > 0 } baked)
            {
                // New run path: use baked labels → profile resolution → full MatchLabels as key
                var profile = ProfileResolver.ResolveByRequiredLabels(profiles, baked.ToList());
                selectorLabels = profile?.MatchLabels ?? baked;
            }
            else
            {
                // Legacy run path: QueuedRequiredLabels was null — do NOT run Superset-match.
                if (!string.IsNullOrWhiteSpace(liveConfig.DefaultRequiredAgentLabels))
                {
                    var defaultLabels = liveConfig.DefaultRequiredAgentLabels
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .ToList()
                        .AsReadOnly();
                    var defaultProfile = ProfileResolver.ResolveByRequiredLabels(profiles, defaultLabels);
                    // TODO: If defaultProfile is null (no enabled profile's MatchLabels is a superset of
                    // defaultLabels), selectorLabels falls back to the raw defaultLabels string (e.g.
                    // "kiro,dotnet"). If no job template matches that partial key (e.g. the only template
                    // is "kiro,dotnet,dotnet10"), dispatch returns 422 and the run is permanently cascaded
                    // to Failed — even though the configuration is technically correct (the profile exists
                    // but the default label string is not the full MatchLabels). Fix: if defaultProfile is
                    // null, log a warning and fall through to the empty-selector path rather than using the
                    // partial raw labels as a key. See review-findings.md [WARNING] ConsolidationDispatcher.cs:100.
                    selectorLabels = defaultProfile?.MatchLabels ?? defaultLabels;
                    Log.Warning(
                        "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels; fell back to DefaultRequiredAgentLabels → selector '{Selector}'",
                        run.RunId, AgentSelectorKey.From(selectorLabels));
                }
                else
                {
                    // No baked labels and no default configured — cannot determine a correct selector.
                    // Produce an empty selector; dispatch will return a permanent 422 (no template).
                    // The permanent-failure handler below will cascade this run to Failed so it surfaces
                    // in the Attention view rather than staying Queued silently.
                    selectorLabels = [];
                    Log.Warning(
                        "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels and DefaultRequiredAgentLabels is not configured. " +
                        "Empty selector will be dispatched; this is a permanent failure — run will be cascaded to Failed.",
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
            else if (result.IsPermanentFailure)
            {
                // Permanent failure: no job template for the selector, or the selector is unresolvable.
                // This will never succeed without a configuration change — cascade to Failed so the run
                // surfaces in the Attention view and does not stay Queued forever.
                Log.Warning(
                    "ConsolidationDispatcher: permanent dispatch failure for run {RunId} ({Type}): {Error}. " +
                    "Cascading to Failed — operator must fix configuration (job template / required labels) and re-trigger.",
                    run.RunId, run.Type, result.ErrorMessage);
                // TODO: Use CancellationToken.None (not ct) for this terminal state write. If ct is
                // already cancelled when we reach here (orchestrator pod draining at the exact moment
                // dispatch returned a permanent failure), UpdateRunAsync(ct) will throw
                // OperationCanceledException, the outer catch will log "unexpected error", and the
                // run will silently stay Queued — defeating the cascade. The terminal bookkeeping
                // write must complete regardless of the inbound token. Only the DistributeAsync call
                // above should honour ct. See review-findings.md [WARNING] ConsolidationDispatcher.cs:135.
                await _consolidationService.UpdateRunAsync(
                    new RunId(run.RunId),
                    ConsolidationRunStatus.Failed,
                    $"Dispatch permanently failed: {result.ErrorMessage}",
                    ct);
            }
            else
            {
                // Transient failure (concurrency limit, PVC unavailable). Leave Queued —
                // startup rehydration retries on next pod restart.
                Log.Warning(
                    "ConsolidationDispatcher: dispatch failed (transient) for run {RunId} ({Type}): {Error}. " +
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
