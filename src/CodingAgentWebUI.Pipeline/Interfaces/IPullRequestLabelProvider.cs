namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Pull request label operations.
/// Consumed exclusively by <c>LabelService</c> for swapping and ensuring agent labels on PRs.
/// </summary>
public interface IPullRequestLabelProvider : IAsyncDisposable
{
    /// <summary>Adds a label to a pull request. Default throws <see cref="NotSupportedException"/>.</summary>
    Task AddPrLabelAsync(int prNumber, string label, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support AddPrLabelAsync.");

    /// <summary>Removes a label from a pull request. Default throws <see cref="NotSupportedException"/>.</summary>
    Task RemovePrLabelAsync(int prNumber, string label, CancellationToken ct)
        => throw new NotSupportedException(
            $"{GetType().Name} does not support RemovePrLabelAsync.");

    /// <summary>
    /// Ensures the agent status labels exist for pull requests. Creates any that are missing.
    /// On GitHub this is a no-op (PRs share labels with issues). Default returns true.
    /// </summary>
    Task<bool> EnsureAgentLabelsForPullRequestsAsync(CancellationToken ct)
        => Task.FromResult(true);
}
