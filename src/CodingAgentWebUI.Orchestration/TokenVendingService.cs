using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Polly.Retry;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Generates short-lived GitHub installation access tokens scoped to specific repositories
/// with minimal permissions (<c>contents: write</c>, <c>pull_requests: write</c>, <c>actions: read</c>).
/// Agents receive these tokens instead of the GitHub App private key.
/// Registered as a singleton in DI.
/// </summary>
public sealed partial class TokenVendingService : ITokenVendingService
{
    private readonly ILogger _logger;
    private readonly HttpClient _httpClient;
    private readonly ResiliencePipeline _httpPipeline;

    // TODO: [RES-07] Singleton owns HttpClient but does not implement IDisposable — DI container cannot clean it up on shutdown.
    public TokenVendingService(ILogger logger)
        : this(logger, new HttpClient())
    {
    }

    /// <summary>
    /// Internal constructor that accepts an <see cref="HttpClient"/> for testing.
    /// </summary>
    internal TokenVendingService(ILogger logger, HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClient);
        _logger = logger;
        _httpClient = httpClient;
        _httpPipeline = CreateHttpPipeline(logger);
    }

    private static ResiliencePipeline CreateHttpPipeline(ILogger logger)
    {
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 3,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(1),
                ShouldHandle = new PredicateBuilder()
                    .Handle<HttpRequestException>()
                    .Handle<SocketException>()
                    .Handle<TaskCanceledException>(ex => ex.InnerException is TimeoutException),
                OnRetry = args =>
                {
                    logger.Warning(
                        "{Operation} retry {Attempt}/{MaxAttempts} after {Exception}",
                        "GenerateAgentToken",
                        args.AttemptNumber + 1,
                        3,
                        args.Outcome.Exception?.GetType().Name ?? "unknown");
                    return ValueTask.CompletedTask;
                }
            })
            .AddTimeout(TimeSpan.FromSeconds(30))
            .Build();
    }

    /// <summary>
    /// Generates a short-lived GitHub installation access token scoped to the target repository
    /// with <c>contents: write</c>, <c>pull_requests: write</c>, and <c>actions: read</c> permissions.
    /// No <c>issues: write</c> — all issue operations stay on the orchestrator.
    /// </summary>
    /// <param name="repoConfig">Repository provider config containing GitHub App credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A tuple of the token string and its expiration time.</returns>
    public async Task<(string Token, DateTimeOffset ExpiresAt)> GenerateAgentTokenAsync(
        ProviderConfig repoConfig,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(repoConfig);

        var settings = repoConfig.Settings;

        if (!settings.TryGetValue("privateKeyBase64", out var privateKeyBase64) || string.IsNullOrWhiteSpace(privateKeyBase64))
            throw new InvalidOperationException("Repository config is missing 'privateKeyBase64' setting");

        if (!settings.TryGetValue("clientId", out var clientId) || string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Repository config is missing 'clientId' setting");

        if (!settings.TryGetValue("installationId", out var installationIdStr) || !long.TryParse(installationIdStr, out var installationId))
            throw new InvalidOperationException("Repository config is missing or invalid 'installationId' setting");

        var apiUrl = settings.TryGetValue("apiUrl", out var url) ? url.TrimEnd('/') : "https://api.github.com";

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
                Issues = "write"
            }
        };

        // Scope to specific repository if available
        if (settings.TryGetValue("repo", out var repoName) && !string.IsNullOrWhiteSpace(repoName))
        {
            requestBody.Repositories = [repoName];
        }

        var requestJson = JsonSerializer.Serialize(requestBody, TokenRequestJsonContext.Default.TokenRequestBody);
        var requestUrl = $"{apiUrl}/app/installations/{installationId}/access_tokens";

        var responseJson = await _httpPipeline.ExecuteAsync(async token =>
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, requestUrl);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("CodingAgentWebUI-TokenVending", "1.0"));
            request.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, token);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(token);
                throw new HttpRequestException(
                    $"GitHub token exchange failed (HTTP {(int)response.StatusCode}): {errorBody}");
            }

            return await response.Content.ReadAsStringAsync(token);
        }, ct);

        var tokenResponse = JsonSerializer.Deserialize(responseJson, TokenResponseJsonContext.Default.TokenResponseBody)
            ?? throw new InvalidOperationException("Failed to deserialize token response");

        var expiresAt = DateTimeOffset.Parse(tokenResponse.ExpiresAt);

        _logger.Information(
            "Generated scoped agent token for installation {InstallationId}, expires at {ExpiresAt}",
            installationId, expiresAt);

        return (tokenResponse.Token, expiresAt);
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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(configs);
        ArgumentNullException.ThrowIfNull(repoConfigId);

        var result = new List<ProviderConfig>(configs.Count);

        foreach (var config in configs)
        {
            // Any config with privateKeyBase64 needs a short-lived token replacement
            // (repo, brain repo, pipeline provider — all may use GitHub App auth)
            if (config.Settings.ContainsKey("privateKeyBase64"))
            {
                try
                {
                    var (token, expiresAt) = await GenerateAgentTokenAsync(config, ct);

                    var clonedSettings = new Dictionary<string, string>(config.Settings);
                    clonedSettings.Remove("privateKeyBase64");
                    clonedSettings["token"] = token;
                    clonedSettings["tokenExpiresAt"] = expiresAt.ToString("O");

                    result.Add(new ProviderConfig
                    {
                        Id = config.Id,
                        Kind = config.Kind,
                        ProviderType = config.ProviderType,
                        DisplayName = config.DisplayName,
                        Settings = clonedSettings,
                        RepositoryRole = config.RepositoryRole
                    });
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Failed to generate token for config {ConfigId} ({DisplayName}), stripping private key only",
                        config.Id, config.DisplayName);

                    var clonedSettings = new Dictionary<string, string>(config.Settings);
                    clonedSettings.Remove("privateKeyBase64");

                    result.Add(new ProviderConfig
                    {
                        Id = config.Id,
                        Kind = config.Kind,
                        ProviderType = config.ProviderType,
                        DisplayName = config.DisplayName,
                        Settings = clonedSettings,
                        RepositoryRole = config.RepositoryRole
                    });
                }
            }
            else
            {
                // Clone non-repo configs as-is (strip any private keys from non-repo configs too)
                var clonedSettings = new Dictionary<string, string>(config.Settings);
                clonedSettings.Remove("privateKeyBase64");

                result.Add(new ProviderConfig
                {
                    Id = config.Id,
                    Kind = config.Kind,
                    ProviderType = config.ProviderType,
                    DisplayName = config.DisplayName,
                    Settings = clonedSettings,
                    RepositoryRole = config.RepositoryRole
                });
            }
        }

        return result.AsReadOnly();
    }

    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key.
    /// Reuses the same pattern as <c>GitHubAppAuthService.GenerateJwt()</c>.
    /// </summary>
    private static string GenerateJwt(string clientId, string privateKeyBase64)
    {
        // Decode base64 to get the PEM string
        var pemBytes = Convert.FromBase64String(privateKeyBase64);
        var privateKeyPem = Encoding.UTF8.GetString(pemBytes);

        if (!privateKeyPem.Contains("-----BEGIN") || !privateKeyPem.Contains("PRIVATE KEY-----"))
            throw new InvalidOperationException("Decoded content is not a PEM private key");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var securityKey = new RsaSecurityKey(rsa.ExportParameters(true));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            IssuedAt = (now - TimeSpan.FromSeconds(60)).UtcDateTime,
            // 5 minutes — same as GitHubAppAuthService (headroom for clock drift)
            Expires = (now + TimeSpan.FromMinutes(5)).UtcDateTime,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JsonWebTokenHandler();
        var jwt = handler.CreateToken(descriptor);

        return jwt;
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

    [JsonSerializable(typeof(TokenRequestBody))]
    private sealed partial class TokenRequestJsonContext : JsonSerializerContext;

    [JsonSerializable(typeof(TokenResponseBody))]
    private sealed partial class TokenResponseJsonContext : JsonSerializerContext;
}
