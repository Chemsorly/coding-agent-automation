using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Registry.SweepPhases;

/// <summary>
/// Phase 0: Chat-agent exemption check.
/// Chat agents do not send periodic heartbeats, so they must never be swept to
/// Disconnected on a stale heartbeat. Returns <c>true</c> (consume agent) for any
/// agent carrying the <c>chat=true</c> label, optionally logging a warning when the
/// registration is unusually old (potential leaked pod registration).
/// </summary>
internal sealed class ChatAgentSweepPhase : ISweepPhase
{
    private readonly ILogger _logger;

    public ChatAgentSweepPhase(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    public Task<bool> ExecuteAsync(AgentEntry agent, DateTimeOffset now, PipelineConfiguration config, CancellationToken ct)
    {
        if (!IsChatAgent(agent))
            return Task.FromResult(false);

        // Chat agents are exempt from heartbeat sweeping (they don't send periodic heartbeats).
        // Log a warning if the agent has been registered for an unusually long time —
        // this may indicate a leaked registration after a pod crash.
        var registeredAge = now - agent.RegisteredAt;
        if (registeredAge > TimeSpan.FromHours(4))
        {
            _logger.Warning(
                "Chat agent {AgentId} has been registered for {AgeHours:F1}h — " +
                "may be a leaked registration if the pod is no longer running",
                agent.AgentId, registeredAge.TotalHours);
        }

        return Task.FromResult(true);
    }

    private static bool IsChatAgent(AgentEntry agent)
        => agent.Labels?.Any(l => string.Equals(l, "chat=true", StringComparison.OrdinalIgnoreCase)) == true;
}
