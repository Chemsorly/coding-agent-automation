using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using Serilog;
using ILogger = Serilog.ILogger;

namespace KiroWebUI.Pipeline.Services;

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
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(privateKeyBase64);
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(logger);

        _clientId = clientId;
        _installationId = installationId;
        _apiUrl = apiUrl;
        _logger = logger;

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

                var client = new GitHubClient(
                    new ProductHeaderValue("KiroWebUI-Pipeline"),
                    new Uri(_apiUrl))
                {
                    Credentials = new Credentials(jwt, AuthenticationType.Bearer)
                };

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
            Expires = (now + TimeSpan.FromMinutes(10)).UtcDateTime,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JsonWebTokenHandler();
        var jwt = handler.CreateToken(descriptor);

        rsa.Dispose();
        return jwt;
    }
}
