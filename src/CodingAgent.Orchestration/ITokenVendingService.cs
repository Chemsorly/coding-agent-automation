using CodingAgent.Pipeline.Models;

namespace CodingAgent.Orchestration;

/// <summary>
/// Generates short-lived GitHub installation access tokens scoped to specific repositories.
/// Agents receive these tokens instead of the GitHub App private key.
/// </summary>
public interface ITokenVendingService
{
    /// <summary>
    /// Generates a short-lived GitHub installation access token scoped to the target repository
    /// with <c>contents: write</c>, <c>pull_requests: write</c>, and <c>actions: read</c> permissions.
    /// Optionally includes <c>issues: write</c> when <paramref name="includeIssuePermission"/> is true.
    /// </summary>
    /// <param name="repoConfig">Repository provider config containing GitHub App credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="includeIssuePermission">Whether to include issues:write permission (default: false).</param>
    /// <returns>A tuple of the token string and its expiration time.</returns>
    Task<(string Token, DateTimeOffset ExpiresAt)> GenerateAgentTokenAsync(
        ProviderConfig repoConfig, CancellationToken ct, bool includeIssuePermission = false);

    /// <summary>
    /// Clones the provided <see cref="ProviderConfig"/> list, replacing <c>privateKeyBase64</c>
    /// with a short-lived <c>token</c> in the Settings dictionary. This ensures agents never
    /// receive the GitHub App private key.
    /// </summary>
    /// <param name="configs">Original provider configs from the configuration store.</param>
    /// <param name="repoConfigId">The repository provider config ID to generate a token for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="includeIssuePermission">Whether to include issues:write permission in vended tokens (default: false).</param>
    /// <returns>Cloned configs with the repo config's private key replaced by a short-lived token.</returns>
    Task<IReadOnlyList<ProviderConfig>> PrepareAgentConfigsAsync(
        IReadOnlyList<ProviderConfig> configs, string repoConfigId, CancellationToken ct, bool includeIssuePermission = false);

    /// <summary>
    /// Prepares the configs of repositories the agent only clones (a project epic's other project
    /// repositories): a <c>contents: read</c> token replaces the GitHub App private key, and the
    /// repository's secrets and setup steps are removed. Configs that cannot get a read-only token
    /// (no GitHub App credentials, or minting fails) are left out, so those repositories are not cloned.
    /// </summary>
    Task<IReadOnlyList<ProviderConfig>> PrepareReadOnlyCloneConfigsAsync(
        IReadOnlyList<ProviderConfig> configs, CancellationToken ct);
}
