using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Resolves a SignalR connection ID for an agent matching the requested labels.
/// Wraps <see cref="AgentRegistryService"/> and <see cref="JobDispatcherService"/> to
/// select an idle, label-compatible agent and reserve it atomically.
/// Registered as singleton in SignalR mode.
/// </summary>
public sealed class SignalRWorkDistributorAgentResolver : ISignalRWorkDistributorAgentResolver
{
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;

    /// <summary>Tracks the last resolved agent ID for revert on failure.</summary>
    private string? _lastResolvedAgentId;

    /// <inheritdoc />
    public string? LastResolvedAgentId => _lastResolvedAgentId;

    public SignalRWorkDistributorAgentResolver(
        AgentRegistryService registry,
        JobDispatcherService dispatcher)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dispatcher);

        _registry = registry;
        _dispatcher = dispatcher;
    }

    /// <inheritdoc />
    public string? ResolveConnectionId(string agentSelector)
    {
        var requiredLabels = string.IsNullOrWhiteSpace(agentSelector)
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : agentSelector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var agent = _dispatcher.SelectAgent(requiredLabels);
        _lastResolvedAgentId = agent?.AgentId;
        return agent?.ConnectionId;
    }

    /// <inheritdoc />
    public void ReleaseLastResolvedAgent()
    {
        var agentId = _lastResolvedAgentId;
        if (agentId is null) return;

        _lastResolvedAgentId = null;
        _registry.TransitionStatus(agentId, AgentStatus.Idle);
    }
}
