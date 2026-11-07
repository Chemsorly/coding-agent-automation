namespace CodingAgent.Contracts.Ui;

/// <summary>
/// Spec 057: one row of Settings → Agents → Capacity — all agents (<see cref="TemplateKey"/> null) or one job template.
/// </summary>
/// <param name="TemplateKey">The job template key (<c>""</c> = the template without labels); null for "All agents".</param>
/// <param name="Tool">The template's tool id; empty for "All agents" or a template without a tool.</param>
/// <param name="RunningRuns">Running agent Jobs of pipeline work (runs of any type).</param>
/// <param name="RunningChats">Running chat Jobs.</param>
/// <param name="HelmLimit">The Helm limit; 0 = none.</param>
/// <param name="UiLimit">The UI limit; 0 = none.</param>
/// <param name="EffectiveLimit">The limit that applies (the lowest set); null when none.</param>
/// <param name="EffectiveSource">Where the limit that applies comes from: <c>helm</c>, <c>ui</c>, or null.</param>
/// <param name="CredentialPausedText">Spec 056: the pause text when the template's credential is paused; null otherwise.</param>
public sealed record UiAgentCapacityRow(
    string? TemplateKey,
    string Tool,
    int RunningRuns,
    int RunningChats,
    int HelmLimit,
    int UiLimit,
    int? EffectiveLimit,
    string? EffectiveSource,
    string? CredentialPausedText = null);

/// <summary>Spec 057: what the Capacity page shows.</summary>
/// <param name="All">The row for all agents together.</param>
/// <param name="Templates">One row per deployed job template, ordered by key.</param>
/// <param name="UnknownTemplateRuns">Running run Jobs whose template is not deployed (count toward "All agents").</param>
/// <param name="UnknownTemplateChats">Running chat Jobs whose template is not deployed.</param>
/// <param name="CountsAvailable">False when the agent Jobs could not be listed; the counts are then 0.</param>
public sealed record UiAgentCapacityPage(
    UiAgentCapacityRow All,
    IReadOnlyList<UiAgentCapacityRow> Templates,
    int UnknownTemplateRuns,
    int UnknownTemplateChats,
    bool CountsAvailable);
