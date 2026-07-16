using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Recovers orphaned agent state during registration. Handles three scenarios:
/// 1. Agent reports an active job that the orchestrator doesn't know about (restore from agent state)
/// 2. Agent registers without active job but orchestrator has runs for it (orphan detection)
/// 3. Agent registers without active job but registry has ActiveJobId (crash recovery)
/// </summary>
public interface IAgentOrphanRecoveryService
{
    /// <summary>
    /// Reconciles agent state after registration. Called immediately after <c>_facade.Register()</c>.
    /// </summary>
    Task RecoverOrphanedStateAsync(AgentRegistrationMessage message, string agentId);
}
