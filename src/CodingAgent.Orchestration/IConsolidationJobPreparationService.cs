using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration;

/// <summary>
/// Result of preparing a consolidation job for dispatch.
/// Contains vended provider configs, the resolved repo provider ID, and the fully-resolved
/// pipeline configuration (with project and template overrides applied) needed for both
/// SignalR and K8s dispatch paths.
/// </summary>
public sealed record ConsolidationJobPreparationResult
{
    /// <summary>Provider configs with short-lived tokens replacing private keys.</summary>
    public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }

    /// <summary>Resolved repo provider config ID (from template). Empty if no template/repo.</summary>
    public required string RepoProviderConfigId { get; init; }

    /// <summary>
    /// Pipeline configuration resolved via <see cref="Pipeline.Services.PipelineConfigurationResolver.ResolveAsync"/>,
    /// with project overrides and template overrides applied. Never null — falls back to global
    /// config (no overrides) when no template or owning project is available.
    /// </summary>
    public required PipelineConfiguration PipelineConfiguration { get; init; }
}

/// <summary>
/// Shared consolidation job preparation: resolves provider configs from template,
/// vends scoped GitHub tokens, and determines correct permission scope.
/// Eliminates duplication between <c>ConsolidationDispatchService</c> (SignalR path)
/// and <c>DispatchService</c> (K8s path).
/// </summary>
public interface IConsolidationJobPreparationService
{
    /// <summary>
    /// Resolves provider configs for a consolidation job and vends short-lived tokens.
    /// </summary>
    /// <param name="type">The consolidation run type (determines whether issues:write is included).</param>
    /// <param name="templateId">Template ID to resolve repo/brain/issue providers from.</param>
    /// <param name="agentLabels">Agent labels for profile-based agent config resolution.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Preparation result with vended configs and repo provider ID, or null if template resolution failed.</returns>
    Task<ConsolidationJobPreparationResult> PrepareAsync(
        ConsolidationRunType type,
        TemplateId? templateId,
        IReadOnlyList<string> agentLabels,
        CancellationToken ct);
}
