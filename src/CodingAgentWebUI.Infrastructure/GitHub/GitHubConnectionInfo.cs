namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Value object bundling the connection parameters shared by all GitHub providers:
/// API URL, repository owner, and repository name.
/// Eliminates primitive obsession in provider constructors.
/// </summary>
public sealed record GitHubConnectionInfo
{
    public string ApiUrl { get; }
    public string Owner { get; }
    public string Repo { get; }

    public GitHubConnectionInfo(string apiUrl, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        ApiUrl = apiUrl;
        Owner = owner;
        Repo = repo;
    }
}
