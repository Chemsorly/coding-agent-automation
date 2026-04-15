using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum IssueProviderType { GitHub }

public interface IIssueProvider
{
    IssueProviderType ProviderType { get; }
    Task<IssueDetail> GetIssueAsync(string identifier, CancellationToken ct);
    Task<IReadOnlyList<IssueSummary>> ListOpenIssuesAsync(CancellationToken ct);
    Task<IReadOnlyList<IssueComment>> ListCommentsAsync(string identifier, CancellationToken ct);
    Task PostCommentAsync(string identifier, string body, CancellationToken ct);
}
