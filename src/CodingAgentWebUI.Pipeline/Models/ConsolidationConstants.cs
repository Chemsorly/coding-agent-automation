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
    /// Legacy prefix used for fallback detection of consolidation runs when the full
    /// <c>InitiatedBy</c> value is not available (e.g. missing SummaryJson in history rows).
    /// All consolidation <c>InitiatedBy</c> values start with this prefix.
    /// </summary>
    public const string InitiatedByPrefix = "consolidation";

    /// <summary>
    /// Value for <see cref="JobDistributionRequest.InitiatedBy"/> on consolidation WorkItems
    /// triggered manually via the UI.
    /// </summary>
    public const string InitiatedBy = InitiatedByConstants.ConsolidationManual;
}
