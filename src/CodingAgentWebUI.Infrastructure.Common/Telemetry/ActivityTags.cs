namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Common OpenTelemetry tag key strings shared across the CodingAgentWebUI.Pipeline assembly.
/// Centralises repeated literals to avoid S1192 violations and typo-based inconsistencies.
/// </summary>
internal static class ActivityTags
{
    public const string Outcome = "outcome";
    public const string Decision = "decision";
    public const string Unknown = "unknown";
    public const string Manual = "manual";
}
