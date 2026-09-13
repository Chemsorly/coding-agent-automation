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

            var selectorLabels = ResolveSelector(run, liveConfig, profiles);

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
                // Permanent failure (e.g. no job template for the resolved selector).
                // Retrying will always produce the same outcome — cascade to Failed so the run
                // surfaces in the Attention view instead of staying Queued forever.
                Log.Error(
                    "ConsolidationDispatcher: permanent dispatch failure for run {RunId} ({Type}): {Error}. " +
                    "Cascading run to Failed.",
                    run.RunId, run.Type, result.ErrorMessage);
                await FailRunSafelyAsync(run, result.ErrorMessage ?? "Permanent dispatch failure", ct);
            }
            else
            {
                // Transient failure — leave the run Queued; startup rehydration retries on next pod restart.
                Log.Warning(
                    "ConsolidationDispatcher: transient dispatch failure for run {RunId} ({Type}): {Error}. " +
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

    /// <summary>
    /// Resolves the agent selector labels for the given run.
    /// <para>
    /// Resolution order:
    /// <list type="number">
    ///   <item>Use <see cref="ConsolidationRun.QueuedRequiredLabels"/> if non-empty (baked at trigger time).</item>
    ///   <item>Fall back to <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> (live config),
    ///         then resolve through the profile store.</item>
    ///   <item>If no default is configured, pick the first enabled profile's MatchLabels as the selector.</item>
    ///   <item>If no profiles exist (startup race), return an empty selector and let the dispatch 409 keep
    ///         the run Queued for rehydration on the next restart.</item>
    /// </list>
    /// </para>
    /// <para>
    /// The key invariant: an empty <paramref name="requiredLabels"/> is NEVER passed to
    /// <see cref="ProfileResolver.ResolveByRequiredLabels"/> because Superset-matching with an
    /// empty target set matches ALL enabled profiles, causing an arbitrary (and likely template-less)
    /// profile to be selected. Instead we check whether required labels are available before calling
    /// the resolver.
    /// </para>
    /// </summary>
    private static IReadOnlyList<string> ResolveSelector(
        ConsolidationRun run,
        PipelineConfiguration liveConfig,
        IReadOnlyList<AgentProfile> profiles)
    {
        // 1. QueuedRequiredLabels baked at trigger time — use them directly.
        var baked = run.QueuedRequiredLabels;
        if (baked is { Count: > 0 })
        {
            var profile = ProfileResolver.ResolveByRequiredLabels(profiles, baked);
            // TODO [WARNING]: If no profile matches the baked labels, the fallback returns baked
            // directly as the selector key. If the baked labels are a required-labels subset
            // (e.g. ["kiro","dotnet"]) rather than full MatchLabels (e.g. ["kiro","dotnet","dotnet10"]),
            // the resulting selector may not match any job template → 422 → permanent-failure cascade.
            // Pre-existing behavior (same as old code), but the cascade-to-Failed makes the
            // consequence permanent rather than recoverable. (review-findings.md DotNetSpecialist warning)
            var selector = profile?.MatchLabels ?? baked;
            Log.Debug(
                "ConsolidationDispatcher: run {RunId} using baked QueuedRequiredLabels → selector '{Selector}'",
                run.RunId, AgentSelectorKey.From(selector));
            return selector;
        }

        // 2. Old run (QueuedRequiredLabels was never set, or was null/empty).
        //    Try the live DefaultRequiredAgentLabels fallback.
        if (!string.IsNullOrWhiteSpace(liveConfig.DefaultRequiredAgentLabels))
        {
            var defaultLabels = liveConfig.DefaultRequiredAgentLabels
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList()
                .AsReadOnly();
            var defaultProfile = ProfileResolver.ResolveByRequiredLabels(profiles, defaultLabels);
            var selector = defaultProfile?.MatchLabels ?? defaultLabels;
            Log.Warning(
                "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels; " +
                "fell back to DefaultRequiredAgentLabels → selector '{Selector}'",
                run.RunId, AgentSelectorKey.From(selector));
            return selector;
        }

        // 3. No default labels configured. Pick the first enabled profile explicitly rather
        //    than passing empty labels to ResolveByRequiredLabels (which would match ALL profiles
        //    via Superset and return an arbitrary/wrong one).
        // TODO [WARNING]: FirstOrDefault uses raw list order from LoadAgentProfilesAsync, which is
        // not guaranteed to be priority-sorted. The selection can route to a lower-priority agent
        // when DefaultRequiredAgentLabels is not configured and multiple enabled profiles exist.
        // Fix: order by descending Priority then ascending Id (mirrors ProfileResolver tiebreak)
        // before calling FirstOrDefault. (review-findings.md correctness finding #3)
        // TODO [WARNING]: The selected profile's MatchLabels may have no matching job template,
        // causing dispatch to 422 and cascade the run to Failed — even if another enabled profile
        // DOES have a template. A better approach would prefer a profile whose selector resolves to
        // an existing job template before falling back arbitrarily. (review-findings.md finding #4)
        var firstEnabled = profiles.FirstOrDefault(p => p.Enabled);
        if (firstEnabled is not null)
        {
            Log.Warning(
                "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels and no DefaultRequiredAgentLabels; " +
                "fell back to first enabled profile '{Profile}' → selector '{Selector}'",
                run.RunId, firstEnabled.DisplayName, AgentSelectorKey.From(firstEnabled.MatchLabels));
            return firstEnabled.MatchLabels;
        }

        // 4. No profiles available at all (startup race — profile store not yet populated).
        //    Return an empty selector; dispatch will 422 (no template), and since that's
        //    indistinguishable from a real config gap, we log a specific warning so operators
        //    know this is a transient startup race rather than a configuration error.
        // TODO [WARNING]: An empty selector causes the API to return 422 → KubernetesWorkDistributor
        // catches it as IsPermanentFailure=true → FailRunSafelyAsync cascades run to Failed.
        // The comment below documents "run stays Queued" but the actual code path cascades to
        // Failed — a startup-race (transient) is indistinguishable from a genuine config gap.
        // Fix options: (a) update comment to reflect actual behavior (cascade-to-Failed may be
        // acceptable), or (b) return early without calling DistributeAsync when selector is empty,
        // so the run genuinely stays Queued for ConsolidationRetryBackgroundService to retry.
        // (review-findings.md DotNetSpecialist finding, ConsolidationDispatcher.cs:176)
        Log.Warning(
            "ConsolidationDispatcher: run {RunId} has no QueuedRequiredLabels, no DefaultRequiredAgentLabels, " +
            "and no enabled profiles (startup race?). Empty selector will cause dispatch failure; run stays Queued.",
            run.RunId);
        return [];
    }

    /// <summary>
    /// Transitions the run to <see cref="ConsolidationRunStatus.Failed"/> via
    /// <see cref="IConsolidationService.UpdateRunAsync"/>. Errors are swallowed and logged
    /// so a secondary failure in the status update does not mask the primary dispatch error.
    /// </summary>
    private async Task FailRunSafelyAsync(ConsolidationRun run, string reason, CancellationToken ct)
    {
        try
        {
            await _consolidationService.UpdateRunAsync(
                new RunId(run.RunId),
                ConsolidationRunStatus.Failed,
                $"Dispatch failed permanently: {reason}",
                ct);
            Log.Information(
                "ConsolidationDispatcher: run {RunId} ({Type}) cascaded to Failed.",
                run.RunId, run.Type);
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "ConsolidationDispatcher: failed to cascade run {RunId} to Failed (status update threw). " +
                "Run may remain Queued.",
                run.RunId);
        }
    }
}
