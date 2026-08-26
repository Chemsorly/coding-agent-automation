using NGitLab;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// Encapsulates the NGitLab client construction pattern shared by all GitLab providers.
/// Supports static token, dynamic token provider (for token refresh via OrchestratorProxy),
/// and a pre-built client for testing.
/// When using a dynamic token provider, recreates the client when the token changes
/// (NGitLab's <see cref="GitLabClient"/> takes a static token at construction).
/// Mirrors <see cref="GitHub.GitHubClientProvider"/> for consistency.
/// </summary>
internal sealed class GitLabClientProvider : IAsyncDisposable
{
    /// <summary>
    /// User agent string sent with all GitLab API requests from the pipeline.
    /// </summary>
    internal const string UserAgentValue = "CodingAgentAutomation/1.0";

    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IGitLabClient? _cachedClient;
    private string? _cachedToken;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;
    private readonly string? _staticToken;
    private readonly string? _apiUrl;

    /// <summary>
    /// Creates a provider with a static access token.
    /// The client is constructed immediately and reused for all subsequent calls.
    /// </summary>
    public GitLabClientProvider(string apiUrl, string token)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);

        _apiUrl = apiUrl;
        _staticToken = token;
        _cachedToken = token;
        _cachedClient = CreateClient(apiUrl, token);
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for token refresh scenarios).
    /// The client is created lazily on first call and recreated whenever the token changes.
    /// </summary>
    public GitLabClientProvider(string apiUrl, Func<CancellationToken, Task<string>> tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
    }

    /// <summary>
    /// Creates a provider with a pre-built client for testing.
    /// </summary>
    public GitLabClientProvider(IGitLabClient client)
    {
        ArgumentNullException.ThrowIfNull(client);

        _cachedClient = client;
    }

    /// <summary>
    /// Returns an <see cref="IGitLabClient"/> configured with a current token.
    /// For static token: returns the cached client.
    /// For dynamic token provider: refreshes the token and recreates the client if the token changed.
    /// Thread-safe via semaphore to prevent concurrent token refresh storms.
    /// </summary>
    public async Task<IGitLabClient> GetClientAsync(CancellationToken ct)
    {
        // Static client or test client — return immediately
        if (_tokenProvider is null)
            return _cachedClient!;

        await _semaphore.WaitAsync(ct);
        try
        {
            var token = await _tokenProvider(ct);

            if (token != _cachedToken)
            {
                _cachedClient = CreateClient(_apiUrl!, token);
                _cachedToken = token;
            }

            return _cachedClient!;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Returns a current token string for use in operations that need a raw token
    /// (e.g., LibGit2Sharp clone/push with HTTPS credentials).
    /// </summary>
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
        {
            await _semaphore.WaitAsync(ct);
            try
            {
                var token = await _tokenProvider(ct);

                if (token != _cachedToken)
                {
                    _cachedClient = CreateClient(_apiUrl!, token);
                    _cachedToken = token;
                }

                return _cachedToken!;
            }
            finally
            {
                _semaphore.Release();
            }
        }

        if (_staticToken is null)
            throw new InvalidOperationException(
                "Token not available for test-constructed providers. Git network operations require a real token.");

        return _staticToken;
    }

    /// <summary>
    /// Creates a new <see cref="GitLabClient"/> with retry disabled (Polly is the sole retry controller)
    /// and a custom user agent.
    /// </summary>
    private static IGitLabClient CreateClient(string apiUrl, string token)
    {
        var options = new RequestOptions(retryCount: 0, retryInterval: TimeSpan.Zero)
        {
            UserAgent = UserAgentValue
        };

        return new GitLabClient(apiUrl, token, options);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        _semaphore.Dispose();
        return ValueTask.CompletedTask;
    }
}
