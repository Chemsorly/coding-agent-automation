using Octokit;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Base class for GitHub providers that share common authentication,
/// client management, and repository validation patterns.
/// </summary>
public abstract class GitHubProviderBase : IAsyncDisposable
{
    private readonly GitHubClientProvider _clientProvider;

    /// <summary>Repository owner.</summary>
    protected string Owner { get; }

    /// <summary>Repository name.</summary>
    protected string Repo { get; }

    protected GitHubProviderBase(string apiUrl, string token, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(apiUrl, token);
        Owner = owner;
        Repo = repo;
    }

    protected GitHubProviderBase(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(apiUrl, tokenProvider);
        Owner = owner;
        Repo = repo;
    }

    protected GitHubProviderBase(IGitHubClient client, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(client);
        Owner = owner;
        Repo = repo;
    }

    protected GitHubProviderBase(IGitHubClient client, string token, string owner, string repo)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);
        _clientProvider = new GitHubClientProvider(client, token);
        Owner = owner;
        Repo = repo;
    }

    /// <summary>API base URL from the client provider.</summary>
    protected string? ApiUrl => _clientProvider.ApiUrl;

    /// <summary>Returns a GitHubClient configured with a current token.</summary>
    protected Task<IGitHubClient> GetClientAsync(CancellationToken ct)
        => _clientProvider.GetClientAsync(ct);

    /// <summary>Returns a current token.</summary>
    protected Task<string> GetTokenAsync(CancellationToken ct)
        => _clientProvider.GetTokenAsync(ct);

    /// <inheritdoc />
    public virtual async Task ValidateAsync(CancellationToken ct)
    {
        var client = await GetClientAsync(ct);
        await client.Repository.Get(Owner, Repo);
    }

    /// <inheritdoc />
    public virtual ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
