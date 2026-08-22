using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Atomically reserves an idle agent for dispatch, preventing two dispatch paths from
/// double-booking the same agent. Registered as a singleton in DI.
/// </summary>
/// <remarks>
/// The name is historical. This type no longer performs deduplication — the in-memory job queue
/// and processing tracker were removed once the work-distribution modes collapsed to Kubernetes-only.
/// Deduplication is now owned by the partial unique index on
/// <c>WorkItems (IssueIdentifier, IssueProviderConfigId)</c> filtered to non-terminal statuses,
/// plus the <c>IsIssueBeingProcessed</c> check at dispatch time. Consider renaming to
/// <c>AgentReservationService</c>.
/// </remarks>
public sealed class JobDeduplicationGuardService
{
    private readonly IAgentRegistryService _registry;

    private readonly ILogger _logger;

    /// <summary>Serializes agent selection to prevent double-booking. See docs/architecture/concurrency-model.md</summary>
    private readonly object _selectionLock = new();

    public JobDeduplicationGuardService(IAgentRegistryService registry, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    /// <summary>
    /// Selects an idle agent whose labels are a superset of the required labels and
    /// atomically reserves it by transitioning to <see cref="AgentStatus.Busy"/>.
    /// This prevents concurrent dispatch paths from selecting the same agent.
    /// When multiple agents match, selects the one idle longest (FIFO by
    /// <see cref="AgentEntry.LastJobCompletedAt"/>, falling back to <see cref="AgentEntry.RegisteredAt"/>).
    /// </summary>
    /// <returns>The reserved agent (already transitioned to Busy), or <c>null</c> if none available.</returns>
    public AgentEntry? SelectAgent(IReadOnlyList<string> requiredLabels)
    {
        ArgumentNullException.ThrowIfNull(requiredLabels);

        lock (_selectionLock)
        {
            var idleAgents = _registry.GetIdleAgents();

            if (idleAgents.Count == 0)
            {
                _logger.Debug("SelectAgent: no idle agents available (requiredLabels=[{Labels}])",
                    string.Join(", ", requiredLabels));
                return null;
            }

            var compatible = idleAgents
                .Where(agent => !agent.Disabled)
                .Where(agent => LabelMatchHelper.IsLabelMatch(agent.Labels, requiredLabels))
                .OrderBy(agent => agent.LastJobCompletedAt ?? agent.RegisteredAt)
                .ToList();

            if (compatible.Count == 0)
            {
                _logger.Debug("SelectAgent: {IdleCount} idle agent(s) but none match requiredLabels=[{Labels}]",
                    idleAgents.Count, string.Join(", ", requiredLabels));
                return null;
            }

            // Iterate compatible agents with double-check pattern:
            // Lock the entry and verify status is still Idle before transitioning.
            // A concurrent status change (reconciliation, disconnect, manual disable) may have
            // marked the agent non-Idle between GetIdleAgents() and now.
            // Lock ordering: _selectionLock (already held) → entry.SyncRoot (no deadlock risk).
            foreach (var candidate in compatible)
            {
                lock (candidate.SyncRoot)
                {
                    if (candidate.Status != AgentStatus.Idle)
                    {
                        // Race: concurrent status change (reconciliation, disconnect) between snapshot and lock — skip
                        _logger.Debug("SelectAgent: skipping agent {AgentId} — status changed to {Status} before reservation",
                            candidate.AgentId, candidate.Status);
                        continue;
                    }

                    // Atomically reserve the agent so no other dispatch path can select it
                    candidate.Status = AgentStatus.Busy;
                    candidate.BusySince = DateTimeOffset.UtcNow;
                }

                _logger.Debug("SelectAgent: reserved agent {AgentId} for requiredLabels=[{Labels}] ({CompatibleCount} compatible, {IdleCount} idle)",
                    candidate.AgentId, string.Join(", ", requiredLabels), compatible.Count, idleAgents.Count);

                return candidate;
            }

            _logger.Debug("SelectAgent: all {CompatibleCount} compatible agents had status change before reservation (requiredLabels=[{Labels}])",
                compatible.Count, string.Join(", ", requiredLabels));
            return null;
        }
    }

    /// <summary>
    /// Resolves the required agent labels for a repository provider config.
    /// Delegates to <see cref="Pipeline.Services.LabelResolver.ResolveRequiredLabels"/> for the actual logic.
    /// Resolution order: <see cref="ProviderConfig.RequiredLabels"/> property →
    /// <see cref="PipelineConfiguration.DefaultRequiredAgentLabels"/> → empty (any agent).
    /// </summary>
    public static IReadOnlyList<string> ResolveRequiredLabels(
        ProviderConfig? repoConfig,
        PipelineConfiguration pipelineConfig)
    {
        return Pipeline.Services.LabelResolver.ResolveRequiredLabels(repoConfig, pipelineConfig);
    }

}
