namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056: one credential in use and whether it is paused (<c>GET /api/agents/credentials</c>, read by the
/// Scheduler for the <c>pipeline.agent_credential.paused</c> gauge).
/// </summary>
/// <param name="Credential">One of <see cref="AgentCredentials.All"/>.</param>
/// <param name="Paused">True while a pause applies.</param>
public sealed record AgentCredentialStatus(string Credential, bool Paused);
