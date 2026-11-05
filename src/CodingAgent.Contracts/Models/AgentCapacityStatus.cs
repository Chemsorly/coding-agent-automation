namespace CodingAgent.Pipeline.Models;

/// <summary>Spec 057: one row of <see cref="AgentCapacityStatus"/> — all agents (<see cref="TemplateKey"/> null) or one job template.</summary>
/// <param name="TemplateKey">The job template key; null for all agents.</param>
/// <param name="RunningRuns">Running agent Jobs of pipeline work.</param>
/// <param name="RunningChats">Running chat Jobs.</param>
/// <param name="EffectiveLimit">The limit that applies; null when none.</param>
/// <param name="EffectiveSource"><c>helm</c>, <c>ui</c>, or null.</param>
public sealed record AgentCapacityStatusRow(
    string? TemplateKey,
    int RunningRuns,
    int RunningChats,
    int? EffectiveLimit,
    string? EffectiveSource);

/// <summary>
/// Spec 057 req 7.2: running agents and the limits that apply, for the Scheduler leader's
/// <c>workdistribution.agents.*</c> gauges (<c>GET /api/agents/capacity</c>).
/// </summary>
public sealed record AgentCapacityStatus(
    AgentCapacityStatusRow All,
    IReadOnlyList<AgentCapacityStatusRow> Templates,
    int UnknownTemplateRuns,
    int UnknownTemplateChats,
    bool CountsAvailable);
