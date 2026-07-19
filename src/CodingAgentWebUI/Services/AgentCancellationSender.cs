using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Implements <see cref="IAgentCancellationSender"/> by resolving the agent's connection ID
/// from the <see cref="AgentRegistryService"/> and sending a CancelJob message via
/// <see cref="IAgentCommunication"/>. Failures are logged and swallowed — agent may already
/// be disconnected.
/// </summary>
internal sealed class AgentCancellationSender : IAgentCancellationSender
{
    private readonly IAgentRegistryService _registry;
    private readonly IAgentCommunication _agentComm;
    private readonly ILogger _logger;

    public AgentCancellationSender(
        IAgentRegistryService registry,
        IAgentCommunication agentComm,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(agentComm);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _agentComm = agentComm;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task SendCancelJobAsync(AgentId agentId, string runId, CancellationToken ct = default)
    {
        // TODO: ThrowIfNullOrEmpty uses [CallerArgumentExpression] which reports "agentId.Value" as the
        // parameter name instead of "agentId". Consider using the overload with explicit paramName:
        // ArgumentException.ThrowIfNullOrEmpty(agentId.Value, nameof(agentId))
        ArgumentException.ThrowIfNullOrEmpty(agentId.Value);
        ArgumentNullException.ThrowIfNull(runId);

        var agent = _registry.GetByAgentId(agentId.Value);
        if (agent is null) return;

        using var perAgentCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        perAgentCts.CancelAfter(TimeSpan.FromSeconds(2));

        try
        {
            await _agentComm.CancelJobAsync(agent.ConnectionId, runId, perAgentCts.Token);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to send CancelJob to agent {AgentId} for run {RunId}", agentId.Value, runId);
        }
    }
}
