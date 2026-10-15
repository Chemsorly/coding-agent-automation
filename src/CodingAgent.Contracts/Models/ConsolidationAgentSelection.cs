namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The result of resolving an agent selector for a consolidation dispatch request.
/// Carries both the computed agent selector key and, when a profile was matched, the profile's ID
/// so that claim time can restore the exact profile without re-resolving from labels.
/// </summary>
/// <param name="AgentSelector">
/// The computed agent selector: a normalised, comma-separated, ordinally-sorted label string
/// built from the matched profile's <see cref="AgentProfile.JobTemplateKey"/>, or from the
/// resolved labels when no profile could be matched.
/// </param>
/// <param name="ResolvedProfileId">
/// The ID of the matched <see cref="AgentProfile"/>, or <c>null</c> when the selector was
/// derived from labels alone (no profile matched, or no profiles are registered).
/// </param>
public sealed record ConsolidationAgentSelection(string AgentSelector, string? ResolvedProfileId);
