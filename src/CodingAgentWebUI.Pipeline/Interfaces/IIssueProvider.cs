using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

public enum IssueProviderType { GitHub }

/// <summary>
/// Result of creating a new issue via <see cref="IIssueProvider.CreateIssueAsync"/>.
/// </summary>
public sealed record CreatedIssueResult
{
    /// <summary>Issue identifier (e.g., "123").</summary>
    public required string Identifier { get; init; }

    /// <summary>URL to the created issue.</summary>
    public required string Url { get; init; }
}

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
    /// Returns <c>true</c> if all labels were created or already exist, <c>false</c> if any label creation failed.
    /// </summary>
    Task<bool> EnsureAgentLabelsAsync(CancellationToken ct);

    /// <summary>
    /// Validates that the provider is correctly configured and can communicate with its
    /// backing service. Called at pipeline start before any work begins.
    /// </summary>
    Task ValidateAsync(CancellationToken ct);

    /// <summary>
    /// Performs provider initialization: validates credentials/access and creates required resources.
    /// Idempotent — safe to call multiple times.
    /// Returns <c>true</c> if all initialization steps succeeded, <c>false</c> if label creation partially failed.
    /// Throws on credential or access validation failure.
    /// </summary>
    async Task<bool> InitializeAsync(CancellationToken ct)
    {
        await ValidateAsync(ct);
        return await EnsureAgentLabelsAsync(ct);
    }

    /// <summary>
    /// Creates a new issue with the given title, body, and optional labels.
    /// Returns the created issue's identifier and URL.
    /// </summary>
    Task<CreatedIssueResult> CreateIssueAsync(
        string title, string body, IReadOnlyList<string>? labels, CancellationToken ct);
}
