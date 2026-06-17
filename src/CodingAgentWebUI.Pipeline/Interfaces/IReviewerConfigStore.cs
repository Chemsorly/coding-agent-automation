using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IReviewerConfigStore
{
    Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct);
    Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct);
    Task DeleteReviewerConfigAsync(string id, CancellationToken ct);

    /// <summary>
    /// Replaces the entire reviewer configuration collection with the factory defaults
    /// defined in <see cref="PipelineConfiguration.DefaultReviewerConfigurations"/>.
    /// </summary>
    Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct);
}
