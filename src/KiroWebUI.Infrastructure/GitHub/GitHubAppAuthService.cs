using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using Serilog;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Infrastructure.GitHub;

/// <summary>
/// Generates RS256-signed JWTs from a GitHub App private key, exchanges them for
/// short-lived installation access tokens via the GitHub API, and caches tokens
/// with automatic renewal (5-minute buffer before expiry).
/// </summary>
public sealed class GitHubAppAuthService : IGitHubAppAuthService
{
    private readonly string _clientId;
    private readonly long _installationId;
    private readonly string _privateKeyPem;
    private readonly string _apiUrl;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly TimeSpan _renewalBuffer = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Factory for creating the IGitHubClient used to exchange JWTs for installation tokens.
    /// Defaults to creating a real Octokit GitHubClient. Tests can override this via the
    /// internal constructor to avoid real HTTP calls.
    /// </summary>
    private readonly Func<string, Uri, IGitHubClient> _clientFactory;

    // Cache fields: reads outside the semaphore may see slightly stale values,
    // but the double-check inside the semaphore ensures correctness.
    // Worst case is an unnecessary semaphore acquisition.
    private string? _cachedToken;
    private DateTimeOffset _tokenExpiresAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Creates a new auth service for a specific GitHub App installation.
    /// </summary>
    /// <param name="clientId">The GitHub App Client ID (used as JWT issuer).</param>
    /// <param name="installationId">The installation ID for the target org/account.</param>
    /// <param name="privateKeyBase64">Base64-encoded PKCS#1 RSA private key PEM.</param>
    /// <param name="apiUrl">GitHub API base URL (e.g. https://api.github.com).</param>
    /// <param name="logger">Serilog logger instance.</param>
    public GitHubAppAuthService(
        string clientId,
        long installationId,
        string privateKeyBase64,
        string apiUrl,
        ILogger logger)
        : this(clientId, installationId, privateKeyBase64, apiUrl, logger, null)
    {
    }

    /// <summary>
    /// Internal constructor that accepts an optional client factory for testing.
    /// When clientFactory is null, defaults to creating real Octokit GitHubClient instances.
    /// </summary>
    internal GitHubAppAuthService(
        string clientId,
        long installationId,
        string privateKeyBase64,
        string apiUrl,
        ILogger logger,
        Func<string, Uri, IGitHubClient>? clientFactory)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(privateKeyBase64);
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(logger);

        _clientId = clientId;
        _installationId = installationId;
        _apiUrl = apiUrl;
        _logger = logger;
        _clientFactory = clientFactory ?? DefaultClientFactory;

        // Decode base64 to get the PEM string and validate it looks like a PEM key.
        try
        {
            var pemBytes = Convert.FromBase64String(privateKeyBase64);
            _privateKeyPem = System.Text.Encoding.UTF8.GetString(pemBytes);

            // Basic validation: check it contains PEM markers
            if (!_privateKeyPem.Contains("-----BEGIN") || !_privateKeyPem.Contains("PRIVATE KEY-----"))
                throw new FormatException("Decoded content is not a PEM private key");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException("Failed to decode private key from base64", ex);
        }
    }

    private static IGitHubClient DefaultClientFactory(string jwt, Uri apiUri)
    {
        return new GitHubClient(
            GitHubClientProvider.AppProductHeader,
            apiUri)
        {
            Credentials = new Credentials(jwt, AuthenticationType.Bearer)
        };
    }

    /// <inheritdoc />
    public async Task<string> GetTokenAsync(CancellationToken ct)
    {
        // Fast path: return cached token if still valid with buffer
        if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - _renewalBuffer)
        {
            return _cachedToken;
        }

        await _semaphore.WaitAsync(ct);
        try
        {
            // Double-check after acquiring semaphore — another thread may have refreshed
            if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt - _renewalBuffer)
            {
                return _cachedToken;
            }

            try
            {
                var jwt = GenerateJwt();

                var client = _clientFactory(jwt, new Uri(_apiUrl));

                var response = await client.GitHubApps.CreateInstallationToken(_installationId);

                _cachedToken = response.Token;
                _tokenExpiresAt = response.ExpiresAt;

                _logger.Information(
                    "GitHub App installation token acquired, expires at {ExpiresAt}",
                    _tokenExpiresAt);

                return _cachedToken;
            }
            catch (Exception ex)
            {
                // Graceful degradation: if we have a cached token that hasn't fully expired, use it
                if (_cachedToken is not null && DateTimeOffset.UtcNow < _tokenExpiresAt)
                {
                    _logger.Warning(
                        ex,
                        "Failed to renew GitHub App token, using cached token (expires {ExpiresAt})",
                        _tokenExpiresAt);
                    return _cachedToken;
                }

                throw new InvalidOperationException(
                    $"GitHub App token exchange failed: {ex.Message}", ex);
            }
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key.
    /// The JWT is used to authenticate as the App and request installation tokens.
    /// </summary>
    internal string GenerateJwt()
    {
        var rsa = RSA.Create();
        rsa.ImportFromPem(_privateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var securityKey = new RsaSecurityKey(rsa.ExportParameters(true));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _clientId,
            IssuedAt = (now - TimeSpan.FromSeconds(60)).UtcDateTime,
            // GitHub enforces a strict ~10-minute max on the exp claim.
            // Using 5 minutes instead of 10 gives ~5 minutes of headroom for
            // clock drift (common in WSL2/Docker Desktop after host sleep/hibernate).
            Expires = (now + TimeSpan.FromMinutes(5)).UtcDateTime,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JsonWebTokenHandler();
        var jwt = handler.CreateToken(descriptor);

        rsa.Dispose();
        return jwt;
    }
}
