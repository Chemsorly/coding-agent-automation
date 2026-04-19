using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Interfaces;

public enum RepositoryProviderType { GitHub }

public interface IRepositoryProvider
{
    RepositoryProviderType ProviderType { get; }
    Task CloneAsync(string workspacePath, CancellationToken ct);
    Task<string> CreateBranchAsync(string workspacePath, string branchName, CancellationToken ct);
    /// <summary>
    /// Stages all changes, unstages any files matching <paramref name="blacklistedPaths"/>,
    /// then commits the remaining staged files.
    /// Returns the list of file paths that were unstaged due to blacklist matches.
    /// </summary>
    Task<IReadOnlyList<string>> CommitAllAsync(string workspacePath, string message,
        IReadOnlyList<string>? blacklistedPaths, CancellationToken ct);

    /// <summary>Backward-compatible overload with no blacklist.</summary>
    Task CommitAllAsync(string workspacePath, string message, CancellationToken ct) =>
        CommitAllAsync(workspacePath, message, null, ct);
    Task PushBranchAsync(string workspacePath, string branchName, CancellationToken ct);
    Task<string> CreatePullRequestAsync(PullRequestInfo prInfo, CancellationToken ct);
}
