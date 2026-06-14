using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Trace;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ResiliencePipeline _httpPipeline;

    public TokenVendingService(ILogger logger, IHttpClientFactory httpClientFactory)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _httpPipeline = CreateHttpPipeline(logger);
    }

    /// <summary>
    /// Internal constructor that accepts an <see cref="HttpClient"/> for testing.
    /// </summary>
    internal TokenVendingService(ILogger logger, HttpClient httpClient)
        : this(logger, new DelegatingHttpClientFactory(httpClient))
    {
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
                    Activity.Current?.AddEvent(new ActivityEvent("retry", tags: new ActivityTagsCollection
                    {
                        { "attempt", args.AttemptNumber + 1 },
                        { "exception_type", args.Outcome.Exception?.GetType().Name ?? "unknown" }
                    }));
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
    /// Optionally includes <c>issues: write</c> when <paramref name="includeIssuePermission"/> is true
    /// (used for consolidation jobs that create issues directly from the agent).
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("TokenVending.GenerateToken");

        try
        {
            var settings = repoConfig.Settings;

            if (!settings.TryGetValue(ProviderSettingKeys.PrivateKeyBase64, out var privateKeyBase64) || string.IsNullOrWhiteSpace(privateKeyBase64))
                throw new InvalidOperationException("Repository config is missing 'privateKeyBase64' setting");

            if (!settings.TryGetValue(ProviderSettingKeys.ClientId, out var clientId) || string.IsNullOrWhiteSpace(clientId))
                throw new InvalidOperationException("Repository config is missing 'clientId' setting");

            if (!settings.TryGetValue(ProviderSettingKeys.InstallationId, out var installationIdStr) || !long.TryParse(installationIdStr, out var installationId))
                throw new InvalidOperationException("Repository config is missing or invalid 'installationId' setting");

            var apiUrl = settings.TryGetValue(ProviderSettingKeys.ApiUrl, out var url) ? url.TrimEnd('/') : "https://api.github.com";

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

            // Scope to specific repository if available
            if (settings.TryGetValue(ProviderSettingKeys.Repo, out var repoName) && !string.IsNullOrWhiteSpace(repoName))
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

                using var httpClient = _httpClientFactory.CreateClient("TokenVending");
                using var response = await httpClient.SendAsync(request, token);

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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
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
                catch (Exception ex)
                {
                    // Critical provider: primary work repo must have valid credentials
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
            SetupSteps = original.SetupSteps
        };
    }

    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key.
    /// Delegates to <see cref="CodingAgentWebUI.Infrastructure.GitHub.GitHubJwtGenerator"/>.
    /// </summary>
    private static string GenerateJwt(string clientId, string privateKeyBase64)
    {
        return CodingAgentWebUI.Pipeline.GitHub.GitHubJwtGenerator.GenerateFromBase64(clientId, privateKeyBase64);
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
