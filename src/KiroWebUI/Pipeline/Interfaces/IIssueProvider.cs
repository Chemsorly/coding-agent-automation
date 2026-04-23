using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum IssueProviderType { GitHub }

public interface IIssueProvider : IAsyncDisposable
{
    IssueProviderType ProviderType { get; }
    Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct);

    /// <summary>
    /// Lists open issues with optional label filtering.
    /// When <paramref name="labels"/> is null or empty, returns all open issues (no filter).
    /// </summary>
    Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize,
        IReadOnlyList<string>? labels, CancellationToken ct);

    /// <summary>
    /// Convenience overload that lists all open issues without label filtering.
    /// Delegates to the label-filtering overload with <c>labels: null</c>.
    /// </summary>
    Task<PagedResult<IssueSummary>> ListOpenIssuesAsync(int page, int pageSize, CancellationToken ct)
        => ListOpenIssuesAsync(page, pageSize, labels: null, ct);

    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct);
    Task PostCommentAsync(string identifier, string body, CancellationToken ct);
    Task UpdateCommentAsync(string issueIdentifier, string commentId, string body, CancellationToken ct);

    /// <summary>
    /// Adds labels to an issue identified by <paramref name="identifier"/>.
    /// </summary>
    Task AddLabelsAsync(string identifier, IReadOnlyList<string> labels, CancellationToken ct);

    /// <summary>
    /// Closes an issue identified by <paramref name="identifier"/>.
    /// </summary>
    Task CloseIssueAsync(string identifier, CancellationToken ct);

    /// <summary>
    /// Removes a single label from an issue. No-op if the label is not present.
    /// </summary>
    Task RemoveLabelAsync(string identifier, string label, CancellationToken ct);

    /// <summary>
    /// Adds a single label to an issue.
    /// </summary>
    Task AddLabelAsync(string identifier, string label, CancellationToken ct)
        => AddLabelsAsync(identifier, new[] { label }, ct);

    /// <summary>
    /// Checks whether all agent status labels already exist in the repository.
    /// </summary>
    Task<bool> HasAgentLabelsAsync(CancellationToken ct);

    /// <summary>
    /// Ensures the agent status labels exist in the repository. Creates any that are missing.
    /// Idempotent — safe to call multiple times.
    /// </summary>
    Task EnsureAgentLabelsAsync(CancellationToken ct);

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);
}
