using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Resolves a SignalR connection ID for an agent matching the requested labels.
/// Wraps <see cref="AgentRegistryService"/> and <see cref="JobDeduplicationGuardService"/> to
/// select an idle, label-compatible agent and reserve it atomically.
/// Registered as singleton in SignalR mode.
/// </summary>
public sealed class SignalRWorkDistributorAgentResolver : ISignalRWorkDistributorAgentResolver
{
    private readonly IAgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;

    public SignalRWorkDistributorAgentResolver(
        IAgentRegistryService registry,
        JobDeduplicationGuardService dispatcher)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _registry = registry;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public AgentResolveResult? ResolveAgent(string agentSelector)
    {
        var requiredLabels = string.IsNullOrWhiteSpace(agentSelector)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : agentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var agent = _dispatcher.SelectAgent(requiredLabels);
        if (agent is null)
            return null;

        return new AgentResolveResult(agent.ConnectionId, agent.AgentId);
    }

    /// <inheritdoc />
    public void ReleaseAgent(AgentId agentId)
    {
        // TODO: Replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) throughout this class.
        // ThrowIfNull on a struct field reports parameter name "Value" instead of "agentId" in exceptions,
        // making failures harder to diagnose. ThrowIfNullOrEmpty also catches empty strings (default(AgentId)-like states).
        ArgumentNullException.ThrowIfNull(agentId.Value);

        var entry = _registry.GetByAgentId(agentId);
        if (entry is not null)
        {
            // Clear BusySince under SyncRoot — ClearAgentState does not handle this field.
            // Without this, agents retain stale BusySince values that could grant undeserved
            // grace periods and mask legitimately stuck agents on subsequent transitions.
            // TODO: Add test coverage for BusySince being cleared on assignment failure.
            lock (entry.SyncRoot)
            {
                entry.BusySince = null;
            }
        }

        // ClearAgentState acquires SyncRoot, clears ActiveJobId + OrphanRestoredAt, then transitions to Idle.
        // Called outside the BusySince lock block to avoid deadlock (ClearAgentState acquires SyncRoot internally).
        _registry.ClearAgentState(agentId.Value);
    }

    /// <inheritdoc />
    public void AssignJob(AgentId agentId, string jobId)
    {
        // TODO: Same as ReleaseAgent — replace ArgumentNullException.ThrowIfNull(agentId.Value) with
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId)) for a meaningful exception message.
        ArgumentNullException.ThrowIfNull(agentId.Value);
        ArgumentNullException.ThrowIfNull(jobId);

        var entry = _registry.GetByAgentId(agentId);
        if (entry is null)
        {
            return;
        }

        lock (entry.SyncRoot)
        {
            entry.ActiveJobId = jobId;
        }
    }
}
