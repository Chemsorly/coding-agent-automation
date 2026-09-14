using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using OpenTelemetry.Trace;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

/// <summary>
/// Generates short-lived GitHub installation access tokens scoped to specific repositories
/// with minimal permissions (<c>contents: write</c>, <c>pull_requests: write</c>, <c>actions: read</c>).
/// Agents receive these tokens instead of the GitHub App private key.
/// Registered as a singleton in DI.
/// </summary>
public sealed partial class TokenVendingService : ITokenVendingService
{
    private readonly ILogger _logger;
    private readonly IHttpClientFactory _httpClientFactory;

    // ── In-process token cache ───────────────────────────────────────────
    // GitHub installation tokens are valid for up to 1 hour. Caching them here
    // prevents redundant GitHub API calls during assignment polling and agent
    // token-refresh requests within the same process.
    //
    // Design: SemaphoreSlim per cache key (single-flight) + double-check pattern.
    // This mirrors GitHubAppAuthService and avoids the Lazy<Task> footgun where a
    // faulted task is retained indefinitely, preventing retries.
    //
    // Thread-safety: TokenVendingService is registered as a singleton in all three
    // hosts (Api, Web/Orchestrator, Scheduler). ConcurrentDictionary operations are
    // individually atomic; the SemaphoreSlim gates concurrent mints for the same key.

    private readonly ConcurrentDictionary<TokenCacheKey, TokenCacheEntry> _tokenCache = new();
    private readonly ConcurrentDictionary<TokenCacheKey, SemaphoreSlim> _mintSemaphores = new();

    // Use the shared constant so the server-side cache aligns with the agent-side renewal buffer.
    private static readonly TimeSpan _renewalBuffer = TokenRefreshConstants.RenewalBuffer;

    /// <summary>
    /// Composite cache key that scopes a cached token to a specific installation,
    /// repository, permission set, and GitHub host.
    /// All four dimensions must be included: missing any risks serving a token
    /// with the wrong permission scope or to the wrong host.
    /// </summary>
    private readonly record struct TokenCacheKey(
        long InstallationId,
        string? RepoName,
        bool IncludeIssuePermission,
        string ApiUrl);

    private readonly record struct TokenCacheEntry(string Token, DateTimeOffset ExpiresAt);

    // ────────────────────────────────────────────────────────────────────

    public TokenVendingService(ILogger logger, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _logger = logger;
        _httpClientFactory = httpClientFactory;
    }

    /// <summary>
    /// Internal constructor that accepts an <see cref="HttpClient"/> for testing.
    /// </summary>
    internal TokenVendingService(ILogger logger, HttpClient httpClient)
        : this(logger, new DelegatingHttpClientFactory(httpClient))
    {
    }

    /// <summary>
    /// Generates a short-lived GitHub installation access token scoped to the target repository
    /// with <c>contents: write</c>, <c>pull_requests: write</c>, and <c>actions: read</c> permissions.
    /// Optionally includes <c>issues: write</c> when <paramref name="includeIssuePermission"/> is true
    /// (used for consolidation jobs that create issues directly from the agent).
    ///
    /// Tokens are cached in-process for their lifetime minus a 5-minute renewal buffer.
    /// Concurrent callers for the same key are coalesced into a single HTTP call (single-flight).
    /// </summary>
    /// <param name="repoConfig">Repository provider config containing GitHub App credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="includeIssuePermission">Whether to include issues:write permission (default: false).</param>
    /// <returns>A tuple of the token string and its expiration time.</returns>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> GenerateAgentTokenAsync(
        ProviderConfig repoConfig,
        CancellationToken ct,
        bool includeIssuePermission = false)
    {
        ArgumentNullException.ThrowIfNull(repoConfig);

        var settings = repoConfig.Settings;

        // Validate required settings before touching the cache so invalid configs
        // throw immediately rather than producing a cache miss and then failing.
        if (!settings.TryGetValue(ProviderSettingKeys.PrivateKeyBase64, out var privateKeyBase64) || string.IsNullOrWhiteSpace(privateKeyBase64))
        {
            _logger.Error("Repository config {ConfigId} is missing 'privateKeyBase64' setting", repoConfig.Id);
            throw new InvalidOperationException("Repository config is missing 'privateKeyBase64' setting");
        }

        if (!settings.TryGetValue(ProviderSettingKeys.ClientId, out var clientId) || string.IsNullOrWhiteSpace(clientId))
        {
            _logger.Error("Repository config {ConfigId} is missing 'clientId' setting", repoConfig.Id);
            throw new InvalidOperationException("Repository config is missing 'clientId' setting");
        }

        if (!settings.TryGetValue(ProviderSettingKeys.InstallationId, out var installationIdStr) || !long.TryParse(installationIdStr, out var installationId))
        {
            _logger.Error("Repository config {ConfigId} is missing or has invalid 'installationId' setting", repoConfig.Id);
            throw new InvalidOperationException("Repository config is missing or invalid 'installationId' setting");
        }

        var apiUrl = settings.TryGetValue(ProviderSettingKeys.ApiUrl, out var url) ? url.TrimEnd('/') : "https://api.github.com";
        settings.TryGetValue(ProviderSettingKeys.Repo, out var repoName);

        var cacheKey = new TokenCacheKey(installationId, repoName, includeIssuePermission, apiUrl);
        var now = DateTimeOffset.UtcNow;

        // ── Fast path: valid cached entry ────────────────────────────────
        // TODO [WARNING]: The cache key does not include ClientId (GitHub App ID). Two different
        // GitHub App registrations that share the same installationId, repoName, includeIssuePermission,
        // and apiUrl would collide to the same cache entry, causing one app's token to be served to
        // callers of the other. Installation IDs are globally unique in practice, but adding ClientId
        // as a fifth key dimension would enforce this as a structural guarantee rather than relying on
        // GitHub's global uniqueness constraint. (SecurityReviewer warning)
        // TODO [WARNING]: Expired TokenCacheEntry records are never removed from _tokenCache. Once a token
        // passes its ExpiresAt, the cache still holds the entry; the fast-path check (ExpiresAt - now >
        // _renewalBuffer) causes it to be bypassed, but it is never cleaned up. Over long uptimes the
        // dictionary accumulates one stale entry per unique cache key that has ever been used (same
        // bounded key-space as _mintSemaphores). Consider adding a periodic housekeeping path that removes
        // entries where ExpiresAt is in the past, and disposes the corresponding semaphore entries at the
        // same time. (.NET Specialist warning)
        if (_tokenCache.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt - now > _renewalBuffer)
        {
            _logger.Debug(
                "Token cache hit for installation {InstallationId} (expires {ExpiresAt}, remaining {RemainingMin:F1} min)",
                installationId, cached.ExpiresAt, (cached.ExpiresAt - now).TotalMinutes);
            return (cached.Token, cached.ExpiresAt);
        }

        // ── Slow path: acquire per-key semaphore, double-check, then mint ─
        // GetOrAdd is safe here: if two callers race, both may create a SemaphoreSlim,
        // but GetOrAdd returns the winner's instance, so only one semaphore per key is used.
        // The discarded loser instance is never waited on and never acquires a kernel WaitHandle,
        // so the IDisposable leak is negligible in practice.
        // TODO [WARNING]: SemaphoreSlim entries in _mintSemaphores are never removed or disposed.
        // SemaphoreSlim implements IDisposable (kernel WaitHandle allocated on first AvailableWaitHandle
        // access). The dictionary grows monotonically — one entry per unique TokenCacheKey — and is
        // never evicted. In expected deployments the key set is small and bounded, so this is a
        // low-severity slow leak rather than a per-request leak. Mitigation: add a TrimExpiredCacheEntries
        // housekeeping path that also removes and disposes orphaned semaphores, or replace with
        // AsyncKeyedLock for cleaner single-flight without manual IDisposable management. (.NET Specialist warning)
        var sem = _mintSemaphores.GetOrAdd(cacheKey, _ => new SemaphoreSlim(1, 1));
        // TODO [WARNING]: The `await sem.WaitAsync(ct)` call below is placed immediately before the
        // `try` block so that if WaitAsync is cancelled, the OCE propagates *before* entering the try,
        // meaning `finally { sem.Release(); }` is never reached — which is correct because the slot was
        // never acquired. However, this correctness depends on the structural placement of a single
        // await line relative to the try boundary. If a future refactor moves WaitAsync inside the try
        // block without adding an `acquired` guard, sem.Release() will be called on an un-acquired
        // semaphore, throwing SemaphoreFullException and defeating Fix A. The standard guard pattern:
        //   bool acquired = false;
        //   try { await sem.WaitAsync(ct); acquired = true; ... }
        //   finally { if (acquired) sem.Release(); }
        // would make the intent explicit and survive such a refactor. (.NET Specialist warning)
        await sem.WaitAsync(ct);
        try
        {
            // Double-check: another caller may have minted while we waited.
            now = DateTimeOffset.UtcNow;
            if (_tokenCache.TryGetValue(cacheKey, out cached) && cached.ExpiresAt - now > _renewalBuffer)
            {
                _logger.Debug(
                    "Token cache hit (post-semaphore) for installation {InstallationId}",
                    installationId);
                return (cached.Token, cached.ExpiresAt);
            }

            // Mint a fresh token from GitHub.
            var (token, expiresAt) = await MintTokenFromGitHubAsync(
                installationId, apiUrl, clientId, privateKeyBase64, repoName, includeIssuePermission, ct);

            // Store in cache. If mint threw, we never reach here, so no faulted entry is stored.
            _tokenCache[cacheKey] = new TokenCacheEntry(token, expiresAt);

            return (token, expiresAt);
        }
        finally
        {
            sem.Release();
        }
    }

    /// <summary>
    /// Performs the actual HTTP call to the GitHub installations API to mint a token.
    /// Separated from <see cref="GenerateAgentTokenAsync"/> to keep the caching logic clear.
    /// Telemetry and error recording live here.
    /// </summary>
    private async Task<(string Token, DateTimeOffset ExpiresAt)> MintTokenFromGitHubAsync(
        long installationId,
        string apiUrl,
        string clientId,
        string privateKeyBase64,
        string? repoName,
        bool includeIssuePermission,
        CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("TokenVending.GenerateToken");

        try
        {
            // Generate JWT (same pattern as GitHubAppAuthService)
            var jwt = GenerateJwt(clientId, privateKeyBase64);

            // Build the scoped token request body
            var requestBody = new TokenRequestBody
            {
                Permissions = new TokenPermissions
                {
                    Contents = "write",
                    PullRequests = "write",
                    Actions = "read",
                    Issues = includeIssuePermission ? "write" : null
                }
            };

            if (!string.IsNullOrWhiteSpace(repoName))
            {
                requestBody.Repositories = [repoName];
            }

            var requestJson = JsonSerializer.Serialize(requestBody, TokenRequestJsonContext.Default.TokenRequestBody);
            var requestUrl = $"{apiUrl}/app/installations/{installationId}/access_tokens";

            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodingAgent.Web-TokenVending", "1.0"));
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            // Note: do not dispose the HttpClient obtained from IHttpClientFactory —
            // the factory manages handler lifetime. Disposing here would corrupt
            // subsequent calls on the same factory (test scenario) or the same pooled
            // handler (production scenario).
            var httpClient = _httpClientFactory.CreateClient("TokenVending");
            using var response = await httpClient.SendAsync(request, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(ct);
                // TODO [WARNING]: errorBody is logged and embedded in the exception message without length capping.
                // For 4xx responses GitHub may reflect request content in the error body, which could include
                // credential-adjacent data. Cap errorBody length before logging to prevent log injection from
                // very large responses and limit exposure of upstream diagnostic information. (SecurityReviewer warning)
                _logger.Error("GitHub token exchange failed for installation {InstallationId} with HTTP {StatusCode}: {ErrorBody}",
                    installationId, (int)response.StatusCode, errorBody);
                throw new HttpRequestException(
                    $"GitHub token exchange failed (HTTP {(int)response.StatusCode}): {errorBody}");
            }

            var responseJson = await response.Content.ReadAsStringAsync(ct);

            var tokenResponse = JsonSerializer.Deserialize(responseJson, TokenResponseJsonContext.Default.TokenResponseBody);
            if (tokenResponse is null)
            {
                _logger.Error("Failed to deserialize GitHub token response for installation {InstallationId}", installationId);
                throw new InvalidOperationException("Failed to deserialize token response");
            }

            var expiresAt = DateTimeOffset.Parse(tokenResponse.ExpiresAt);

            _logger.Information(
                "Generated scoped agent token for installation {InstallationId}, expires at {ExpiresAt}",
                installationId, expiresAt);

            return (tokenResponse.Token, expiresAt);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            // TODO [WARNING]: activity?.AddException(ex) records the full exception onto the OTel trace span.
            // For HttpRequestException failures, ex.Message contains the raw GitHub error response body
            // (constructed with the full errorBody string). Depending on the OTLP collector configuration
            // and who has read access to traces, this may leak upstream diagnostic information.
            // Consider truncating errorBody before embedding it in the exception message, or recording
            // a sanitised summary on the span instead of the full exception. (SecurityReviewer warning)
            activity?.AddException(ex);
            PipelineTelemetry.TokenVendingFailures.Add(1);
            throw;
        }
    }

    /// <summary>
    /// Clones the provided <see cref="ProviderConfig"/> list, replacing <c>privateKeyBase64</c>
    /// with a short-lived <c>token</c> in the Settings dictionary. This ensures agents never
    /// receive the GitHub App private key.
    /// </summary>
    /// <param name="configs">Original provider configs from the configuration store.</param>
    /// <param name="repoConfigId">The repository provider config ID to generate a token for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Cloned configs with the repo config's private key replaced by a short-lived token.</returns>
    public async Task<IReadOnlyList<ProviderConfig>> PrepareAgentConfigsAsync(
        IReadOnlyList<ProviderConfig> configs,
        string repoConfigId,
        CancellationToken ct,
        bool includeIssuePermission = false)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(repoConfigId);

        var result = new List<ProviderConfig>(configs.Count);

        foreach (var config in configs)
        {
            // Any config with privateKeyBase64 needs a short-lived token replacement
            // (repo, brain repo, pipeline provider — all may use GitHub App auth)
            if (config.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64))
            {
                try
                {
                    var (token, expiresAt) = await GenerateAgentTokenAsync(config, ct, includeIssuePermission);

                    var clonedSettings = new Dictionary<string, string>(config.Settings);
                    clonedSettings.Remove(ProviderSettingKeys.PrivateKeyBase64);
                    clonedSettings[ProviderSettingKeys.Token] = token;
                    clonedSettings[ProviderSettingKeys.TokenExpiresAt] = expiresAt.ToString("O");

                    result.Add(CloneWithSettings(config, clonedSettings));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Critical provider: primary work repo must have valid credentials.
                    // OperationCanceledException is excluded from this catch so that a
                    // caller cancellation is not laundered into an "Aborting dispatch" error —
                    // a client timeout is not a token-generation failure.
                    // TODO: Issue provider configs are not passed to this method (handled separately in PrepareIssueContextAsync).
                    //       If issue provider configs are ever added to this path, extend this check to treat them as critical.
                    if (config.Id == repoConfigId)
                    {
                        _logger.Error(ex, "Token generation failed for critical provider {ConfigId} ({DisplayName}). Aborting dispatch.",
                            config.Id, config.DisplayName);
                        throw new InvalidOperationException(
                            $"Token generation failed for critical provider '{config.DisplayName}' (ID: {config.Id}): {ex.Message}", ex);
                    }

                    // Non-critical provider (brain, pipeline, additional repos): degrade gracefully
                    // on genuine token-generation failures (invalid PEM, network error, HTTP 5xx).
                    // OperationCanceledException is NOT caught here — cancellation propagates so that
                    // a torn-down request does not yield a partial config list.
                    _logger.Warning(ex, "Failed to generate token for config {ConfigId} ({DisplayName}), stripping private key only",
                        config.Id, config.DisplayName);

                    var clonedSettings = new Dictionary<string, string>(config.Settings);
                    clonedSettings.Remove(ProviderSettingKeys.PrivateKeyBase64);

                    result.Add(CloneWithSettings(config, clonedSettings));
                }
            }
            else
            {
                // Clone non-GitHub-App configs as-is (strip any private keys)
                var clonedSettings = new Dictionary<string, string>(config.Settings);
                clonedSettings.Remove(ProviderSettingKeys.PrivateKeyBase64);

                // GitLab: copy AccessToken to standard token key for agent consumption.
                // KNOWN LIMITATION: The GitLab access token is passed through in plaintext.
                // Unlike GitHub App tokens (which are short-lived and scoped), GitLab PATs are
                // long-lived. Recommend using short-lived project access tokens (max 1-day expiry)
                // to minimize exposure if an agent is compromised.
                if (clonedSettings.TryGetValue(ProviderSettingKeys.AccessToken, out var accessToken)
                    && !string.IsNullOrWhiteSpace(accessToken))
                {
                    clonedSettings[ProviderSettingKeys.Token] = accessToken;
                    clonedSettings.Remove(ProviderSettingKeys.AccessToken);
                }

                result.Add(CloneWithSettings(config, clonedSettings));
            }
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Creates a new <see cref="ProviderConfig"/> copying all properties from the original,
    /// replacing only the <see cref="ProviderConfig.Settings"/> dictionary.
    /// This ensures newer properties (Secrets, SetupSteps, RequiredLabels, etc.) are never
    /// accidentally dropped when cloning configs during token vending.
    /// </summary>
    private static ProviderConfig CloneWithSettings(ProviderConfig original, Dictionary<string, string> newSettings)
    {
        return new ProviderConfig
        {
            Id = original.Id,
            Kind = original.Kind,
            ProviderType = original.ProviderType,
            DisplayName = original.DisplayName,
            Settings = newSettings,
            RepositoryRole = original.RepositoryRole,
            RequiredLabels = original.RequiredLabels,
            BlacklistedPaths = original.BlacklistedPaths,
            Secrets = original.Secrets,
            SetupSteps = original.SetupSteps,
            SteeringContent = original.SteeringContent
        };
    }

    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key.
    /// Delegates to <see cref="CodingAgent.Infrastructure.GitHub.GitHubJwtGenerator"/>.
    /// </summary>
    private static string GenerateJwt(string clientId, string privateKeyBase64)
    {
        return CodingAgent.Pipeline.GitHub.GitHubJwtGenerator.GenerateFromBase64(clientId, privateKeyBase64);
    }

    // ── JSON serialization types for GitHub API ─────────────────────────

    private sealed class TokenRequestBody
    {
        [JsonPropertyName("permissions")]
        public TokenPermissions? Permissions { get; set; }

        [JsonPropertyName("repositories")]
        public List<string>? Repositories { get; set; }
    }

    private sealed class TokenPermissions
    {
        [JsonPropertyName("contents")]
        public string? Contents { get; set; }

        [JsonPropertyName("pull_requests")]
        public string? PullRequests { get; set; }

        [JsonPropertyName("actions")]
        public string? Actions { get; set; }

        [JsonPropertyName("issues")]
        public string? Issues { get; set; }
    }

    private sealed class TokenResponseBody
    {
        [JsonPropertyName("token")]
        public string Token { get; set; } = "";

        [JsonPropertyName("expires_at")]
        public string ExpiresAt { get; set; } = "";
    }

    [JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(TokenRequestBody))]
    private sealed partial class TokenRequestJsonContext : JsonSerializerContext;

    [JsonSerializable(typeof(TokenResponseBody))]
    private sealed partial class TokenResponseJsonContext : JsonSerializerContext;

    /// <summary>
    /// Wraps a pre-existing <see cref="HttpClient"/> as an <see cref="IHttpClientFactory"/> for testing.
    /// </summary>
    private sealed class DelegatingHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}
