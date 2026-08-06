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
        // TODO: The implicit string→AgentId conversion above calls ArgumentException.ThrowIfNullOrEmpty.
        // If AgentEntry.AgentId is ever null or empty (e.g., due to a registration bug or a future model
        // change), this throws ArgumentException in a hot dispatch path whose callers only check for null
        // return. Once AgentEntry.AgentId is migrated to AgentId type (see AgentEntry.cs TODO), this path
        // will be safe. Until then, a malformed AgentEntry could cause an unexpected exception here.
    }

    /// <inheritdoc />
    public void ReleaseAgent(AgentId agentId)
    {
        var entry = _registry.GetByAgentId(agentId);
        if (entry is not null)
        {
            lock (entry.SyncRoot)
            {
                entry.ActiveJobId = null;
                // TODO: Add test coverage for BusySince being cleared on assignment failure.
                // Without this, agents retain stale BusySince values that could grant undeserved
                // grace periods and mask legitimately stuck agents on subsequent transitions.
                entry.BusySince = null;
            }
        }

        _registry.TransitionStatus(agentId, AgentStatus.Idle);
    }

    /// <inheritdoc />
    public void AssignJob(AgentId agentId, string jobId)
    {
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
