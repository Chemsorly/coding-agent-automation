using Octokit;

namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Encapsulates the dual-auth <see cref="IGitHubClient"/> construction pattern shared by all GitHub providers.
/// Supports both static token and dynamic token provider (GitHub App auth), as well as
/// a pre-built client for testing.
/// Caches tokens from the dynamic provider to avoid redundant refresh calls on every API operation.
/// </summary>
internal class GitHubClientProvider
{
    /// <summary>
    /// Shared product header used by all GitHub API calls from the pipeline.
    /// Defined once here to eliminate duplicate string literals across providers.
    /// </summary>
    internal static readonly ProductHeaderValue AppProductHeader = new("CodingAgentWebUI-Pipeline");

    private readonly IGitHubClient? _staticClient;
    private readonly string? _apiUrl;
    private readonly string? _staticToken;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// The API URL used for client construction, or null if a static client was provided.
    /// Exposed for providers that need the URL for non-Octokit operations (e.g., deriving clone URLs).
    /// </summary>
    internal string? ApiUrl => _apiUrl;

    /// <summary>
    /// Creates a provider with a dynamic token provider (for GitHub App auth via <see cref="Services.GitHubAppAuthService"/>).
    /// </summary>
    public GitHubClientProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubClientProvider(string apiUrl, string token, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);

        _apiUrl = apiUrl;
        _staticToken = token;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _staticClient = new GitHubClient(AppProductHeader, new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
    }

    /// <summary>
    /// Creates a provider with a pre-built client for testing.
    /// Optionally accepts a token for providers that need raw token access (e.g., LibGit2Sharp operations).
    /// </summary>
    public GitHubClientProvider(IGitHubClient staticClient, string? token = null, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(staticClient);

        _staticClient = staticClient;
        _staticToken = token;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns a <see cref="IGitHubClient"/> configured with a current token.
    /// If a token provider is set, uses a cached token (refreshing only when near expiry).
    /// Otherwise, returns the static client.
    /// </summary>
    public async Task<IGitHubClient> GetClientAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
        {
            // Always delegate to the token provider (GitHubAppAuthService) which has its own
            // cache with real expiry tracking. No local caching here — avoids stale-token races
            // between long-lived poller providers and short-lived dispatch providers that share
            // the same underlying GitHubAppAuthService instance.
            var token = await _tokenProvider(ct);
            return new GitHubClient(AppProductHeader, new Uri(_apiUrl!))
            {
                Credentials = new Credentials(token)
            };
        }

        return _staticClient!;
    }

    /// <summary>
    /// Returns a current token string, either from the static field or from the dynamic token provider.
    /// Used by <see cref="GitHubRepositoryProvider"/> for LibGit2Sharp operations (clone, push)
    /// that need a raw token rather than an <see cref="IGitHubClient"/>.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
            return await _tokenProvider(ct);

        return _staticToken!;
    }
}
