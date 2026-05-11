namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Encapsulates the pattern: resolve issue provider config → create provider →
/// remove all agent labels → add new label → dispose provider.
/// All operations are best-effort (failures are logged, not thrown).
/// </summary>
public interface IIssueProviderLabelSwapper
{
    /// <summary>
    /// Swaps the agent label on an issue: removes all existing agent labels, then adds <paramref name="newLabel"/>.
    /// Resolves the issue provider from config. Failures are caught and logged.
    /// </summary>
    Task SwapLabelAsync(string issueProviderConfigId, string issueIdentifier, string newLabel, CancellationToken ct);
}
