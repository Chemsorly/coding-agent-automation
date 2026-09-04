namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Snapshot of the Kiro credential (PVC) pool for the Fleet screen: how many credential slots are
/// configured, how many are free, and how many are currently claimed by active work. When
/// <see cref="Total"/> is 0 the pool isn't configured (single-credential / non-Kubernetes mode),
/// so the UI should treat it as "not pooled" rather than "exhausted".
/// </summary>
/// <param name="Total">Configured credential-pool slots (KiroPvcPool size).</param>
/// <param name="Available">Slots not currently claimed by pending/dispatched/running work.</param>
/// <param name="Claimed">Slots claimed by active work items.</param>
public sealed record CredentialPoolStatus(int Total, int Available, int Claimed);
