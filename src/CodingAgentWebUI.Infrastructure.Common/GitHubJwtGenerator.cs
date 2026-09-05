using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace CodingAgentWebUI.Pipeline.GitHub;

/// <summary>
/// Shared helper for generating RS256-signed JWTs for GitHub App authentication.
/// Used by both <c>GitHubAppAuthService</c> (orchestrator-side token caching)
/// and <c>TokenVendingService</c> (agent-side scoped token generation).
/// </summary>
public static class GitHubJwtGenerator
{
    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key (already decoded PEM string).
    /// </summary>
    /// <param name="clientId">The GitHub App Client ID (used as JWT issuer).</param>
    /// <param name="privateKeyPem">The decoded PEM private key string.</param>
    /// <returns>A signed JWT string valid for 5 minutes.</returns>
    public static string GenerateFromPem(string clientId, string privateKeyPem)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(privateKeyPem);

        using var rsa = RSA.Create();
        rsa.ImportFromPem(privateKeyPem);

        var now = DateTimeOffset.UtcNow;
        var securityKey = new RsaSecurityKey(rsa.ExportParameters(true));
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = clientId,
            // Backdate by 60 seconds to account for clock skew between machines
            IssuedAt = (now - TimeSpan.FromSeconds(60)).UtcDateTime,
            // GitHub enforces a strict ~10-minute max on the exp claim.
            // Using 5 minutes gives ~5 minutes of headroom for clock drift
            // (common in WSL2/Docker Desktop after host sleep/hibernate).
            Expires = (now + TimeSpan.FromMinutes(5)).UtcDateTime,
            SigningCredentials = new SigningCredentials(securityKey, SecurityAlgorithms.RsaSha256)
        };

        var handler = new JsonWebTokenHandler();
        return handler.CreateToken(descriptor);
    }

    /// <summary>
    /// Generates a JWT signed with the GitHub App's private key (base64-encoded PEM).
    /// Decodes the base64 to a PEM string, validates PEM markers, then generates the JWT.
    /// </summary>
    /// <param name="clientId">The GitHub App Client ID (used as JWT issuer).</param>
    /// <param name="privateKeyBase64">Base64-encoded PKCS#1 RSA private key PEM.</param>
    /// <returns>A signed JWT string valid for 5 minutes.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the decoded content is not a valid PEM key.</exception>
    public static string GenerateFromBase64(string clientId, string privateKeyBase64)
    {
        ArgumentNullException.ThrowIfNull(clientId);
        ArgumentNullException.ThrowIfNull(privateKeyBase64);

        var pemBytes = Convert.FromBase64String(privateKeyBase64);
        var privateKeyPem = Encoding.UTF8.GetString(pemBytes);

        if (!privateKeyPem.Contains("-----BEGIN") || !privateKeyPem.Contains("PRIVATE KEY-----"))
        {
            Serilog.Log.Error("Decoded content is not a PEM private key (ClientId={ClientId})", clientId);
            throw new InvalidOperationException("Decoded content is not a PEM private key");
        }

        return GenerateFromPem(clientId, privateKeyPem);
    }
}
