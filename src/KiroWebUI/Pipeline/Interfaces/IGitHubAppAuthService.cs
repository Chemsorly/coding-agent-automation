namespace KiroWebUI.Pipeline.Interfaces;

/// <summary>
/// Generates and caches GitHub App installation access tokens.
/// Thread-safe: concurrent callers share a single token acquisition via internal locking.
/// The cached token is automatically renewed when it approaches expiry (5-minute buffer).
/// </summary>
public interface IGitHubAppAuthService
{
    /// <summary>
    /// Returns a valid installation access token, refreshing if the cached token
    /// has expired or has fewer than 5 minutes remaining.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A valid GitHub installation access token string.</returns>
    Task<string> GetTokenAsync(CancellationToken ct);
}
