using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public interface IReviewerConfigStore
{
    Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct);
    Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct);
    Task DeleteReviewerConfigAsync(string id, CancellationToken ct);
}
