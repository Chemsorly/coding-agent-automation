using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Groups dispatch-time resolution concerns: quality gate and reviewer resolution.
/// Centralises resolution concerns to reduce constructor parameter count.
/// </summary>
public sealed class DispatchResolutionService
{
    private readonly QualityGateResolver _qualityGateResolver;
    private readonly ReviewerResolver _reviewerResolver;

    internal IConfigurationStore ConfigStore { get; }

    public DispatchResolutionService(
        QualityGateResolver qualityGateResolver,
        ReviewerResolver reviewerResolver,
        IConfigurationStore configStore)
    {
        ArgumentNullException.ThrowIfNull(qualityGateResolver);
        ArgumentNullException.ThrowIfNull(reviewerResolver);
        ArgumentNullException.ThrowIfNull(configStore);

        _qualityGateResolver = qualityGateResolver;
        _reviewerResolver = reviewerResolver;
        ConfigStore = configStore;
    }

    /// <summary>
    /// Resolves quality gate configurations matching the job's required labels.
    /// </summary>
    public async Task<IReadOnlyList<QualityGateConfiguration>> ResolveQualityGatesAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var allQgcs = await ConfigStore.LoadQualityGateConfigsAsync(ct);
        return _qualityGateResolver.Resolve(allQgcs, requiredLabels);
    }

    /// <summary>
    /// Resolves reviewer configurations matching the job's required labels.
    /// </summary>
    public async Task<IReadOnlyList<ReviewerConfiguration>> ResolveReviewersAsync(
        IReadOnlyList<string> requiredLabels, CancellationToken ct)
    {
        var allReviewerConfigs = await ConfigStore.LoadReviewerConfigsAsync(ct);
        return _reviewerResolver.Resolve(allReviewerConfigs, requiredLabels);
    }
}
