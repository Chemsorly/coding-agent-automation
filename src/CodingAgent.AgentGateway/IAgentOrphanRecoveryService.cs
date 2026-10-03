using CodingAgent.Pipeline.Models;

namespace CodingAgent.AgentGateway;

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
    // TODO [WARNING]: Parameter named 'ct' deviates from .NET BCL and ASP.NET Core convention
    // ('cancellationToken'). This is a style issue only and does not affect correctness, but
    // interface parameter names form part of the public contract for named-argument callers.
    // Consider renaming to 'cancellationToken' in a future cleanup pass.
    Task<OrphanRecoveryResult> RecoverOrphanedStateAsync(AgentRegistrationMessage message, AgentId agentId, CancellationToken ct = default);
}

/// <summary>What <see cref="IAgentOrphanRecoveryService.RecoverOrphanedStateAsync"/> did that the hub acts on.</summary>
/// <param name="FirstPickupRun">
/// The tracked run the agent was just recorded on as its first agent — the moment a dispatched run is
/// actually picked up — or <see langword="null"/>. Only reported once the agent's claim to the run was
/// accepted.
/// </param>
public readonly record struct OrphanRecoveryResult(PipelineRun? FirstPickupRun);
