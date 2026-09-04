namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A slim per-gate pass/fail outcome captured onto the run summary (flattened from the run's
/// <c>LatestQualityReport</c>). Enables the Insights "which gate fails most" ranking without
/// persisting the full quality-gate report.
/// </summary>
/// <param name="GateName">Display name of the gate (standard gate name, or a quality-gate-command's display name).</param>
/// <param name="Passed">Whether the gate passed on the run's final attempt.</param>
public sealed record GateOutcome(string GateName, bool Passed);
