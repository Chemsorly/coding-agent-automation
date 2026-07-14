namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Well-known sentinel values used to identify consolidation WorkItems in the unified dispatch system.
/// Consolidation jobs share the WorkItem table with pipeline jobs but use synthetic provider IDs
/// since they don't correspond to real issue/repo providers.
/// </summary>
public static class ConsolidationConstants
{
    /// <summary>
    /// Sentinel value for <see cref="JobDistributionRequest.IssueProviderConfigId"/> on consolidation WorkItems.
    /// Used to detect consolidation runs in shared completion/rehydration paths that handle all WorkItem types.
    /// </summary>
    public const string ProviderConfigId = "consolidation";

    /// <summary>
    /// Value for <see cref="JobDistributionRequest.InitiatedBy"/> on consolidation WorkItems.
    /// </summary>
    public const string InitiatedBy = "consolidation";
}
