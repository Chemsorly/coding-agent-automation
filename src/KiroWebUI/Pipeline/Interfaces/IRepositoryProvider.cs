using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum RepositoryProviderType { GitHub }

public interface IRepositoryProvider
{
    RepositoryProviderType ProviderType { get; }
    Task CloneAsync(string workspacePath, CancellationToken ct);
    Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct);
    Task CommitAllAsync(string workspacePath, string message, CancellationToken ct);
    Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct);
    Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct);
}
