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

    /// <summary>
    /// Refresh the token when it's within this duration of the assumed expiry.
    /// GitHub installation tokens last 1 hour; we refresh 5 minutes early to avoid
    /// using a token that expires mid-operation.
    /// </summary>
    private static readonly TimeSpan RefreshBuffer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Assumed token lifetime. GitHub installation tokens expire after 1 hour.
    /// </summary>
    private static readonly TimeSpan AssumedTokenLifetime = TimeSpan.FromMinutes(60);

    private readonly IGitHubClient? _staticClient;
    private readonly string? _apiUrl;
    private readonly string? _staticToken;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;

    // Token cache fields (only used with dynamic token provider)
    private string? _cachedToken;
    private DateTimeOffset _cachedTokenFetchedAt;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);

    /// <summary>
    /// The API URL used for client construction, or null if a static client was provided.
    /// Exposed for providers that need the URL for non-Octokit operations (e.g., deriving clone URLs).
    /// </summary>
    internal string? ApiUrl => _apiUrl;

    /// <summary>
    /// Creates a provider with a dynamic token provider (for GitHub App auth via <see cref="Services.GitHubAppAuthService"/>).
    /// </summary>
    public GitHubClientProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Creates a provider with a static token (backward compatible).
    /// </summary>
    public GitHubClientProvider(string apiUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);

        _apiUrl = apiUrl;
        _staticToken = token;
        _staticClient = new GitHubClient(AppProductHeader, new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
    }

    /// <summary>
    /// Creates a provider with a pre-built client for testing.
    /// Optionally accepts a token for providers that need raw token access (e.g., LibGit2Sharp operations).
    /// </summary>
    public GitHubClientProvider(IGitHubClient staticClient, string? token = null)
    {
        ArgumentNullException.ThrowIfNull(staticClient);

        _staticClient = staticClient;
        _staticToken = token;
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
            var token = await GetCachedTokenAsync(ct);
            return new GitHubClient(AppProductHeader, new Uri(_apiUrl!))
            {
                Credentials = new Credentials(token)
            };
        }

        return _staticClient!;
    }

    /// <summary>
    /// Returns a current token string, either from the static field or from the cached dynamic token.
    /// Used by <see cref="GitHubRepositoryProvider"/> for LibGit2Sharp operations (clone, push)
    /// that need a raw token rather than an <see cref="IGitHubClient"/>.
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
            return await GetCachedTokenAsync(ct);

        return _staticToken!;
    }

    /// <summary>
    /// Returns a cached token, refreshing only when the token is near expiry.
    /// Thread-safe via semaphore to prevent concurrent refresh storms.
    /// </summary>
    private async Task<string> GetCachedTokenAsync(CancellationToken ct)
    {
        // Fast path: token is still valid (no lock needed for read)
        if (_cachedToken is not null && !IsTokenNearExpiry())
            return _cachedToken;

        await _tokenLock.WaitAsync(ct);
        try
        {
            // Double-check after acquiring lock (another thread may have refreshed)
            if (_cachedToken is not null && !IsTokenNearExpiry())
                return _cachedToken;

            _cachedToken = await _tokenProvider!(ct);
            _cachedTokenFetchedAt = DateTimeOffset.UtcNow;
            return _cachedToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private bool IsTokenNearExpiry()
    {
        var elapsed = DateTimeOffset.UtcNow - _cachedTokenFetchedAt;
        return elapsed >= AssumedTokenLifetime - RefreshBuffer;
    }
}
