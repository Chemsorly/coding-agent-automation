namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Stable well-known identifiers used by the pipeline system.
/// </summary>
public static class WellKnownIds
{
    /// <summary>
    /// The stable ID for the Default project, used for the undeletable guard
    /// and startup migration. All-zeros GUID ensures deterministic identity.
    /// </summary>
    public const string DefaultProjectId = "00000000-0000-0000-0000-000000000000";
}
