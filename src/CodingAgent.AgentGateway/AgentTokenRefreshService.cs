using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using System.Globalization;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Handles token refresh logic for agents. Resolves provider configurations from
/// the WorkItem payload in the database, then returns an appropriate token
/// based on the auth mechanism configured.
/// </summary>
internal sealed class AgentTokenRefreshService : IAgentTokenRefreshService
{
    private readonly IAgentHubFacade _facade;
    private readonly ITokenVendingService _tokenVending;
    private readonly ILogger _logger;

    public AgentTokenRefreshService(
        IAgentHubFacade facade,
        ITokenVendingService tokenVending,
        ILogger logger)
    {
        _facade = facade;
        _tokenVending = tokenVending;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<TokenRefreshResponse> RefreshTokenAsync(string jobId, ProviderKind providerKind, CancellationToken ct)
    {
        var (repoProviderConfigId, brainProviderConfigId) = await ResolveProviderConfigIdsAsync(jobId, ct);

        var targetConfig = await ResolveTargetConfigAsync(jobId, providerKind, repoProviderConfigId, brainProviderConfigId, ct);

        return await VendTokenAsync(jobId, providerKind, targetConfig, ct);
    }

    private async Task<(string? repoId, string? brainId)> ResolveProviderConfigIdsAsync(
        string jobId, CancellationToken ct)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
            return (run.RepoProviderConfigId, run.BrainProviderConfigId);

        // Fallback: resolve from WorkItem payload in DB (no in-memory run found)
        var configIds = await _facade.GetWorkItemProviderConfigIdsAsync(jobId, ct);
        if (configIds is null)
        {
            _logger.Warning("No active run or work item found for job {JobId}", jobId);
            throw new HubException($"No active run or work item found for job {jobId}");
        }

        if (string.IsNullOrEmpty(configIds.Value.RepoProviderConfigId))
        {
            _logger.Warning("WorkItem {JobId} has no repoProviderConfigId in payload", jobId);
            throw new HubException($"WorkItem {jobId} has no repoProviderConfigId in payload");
        }

        return (configIds.Value.RepoProviderConfigId, configIds.Value.BrainProviderConfigId);
    }

    private async Task<ProviderConfig> ResolveTargetConfigAsync(
        string jobId, ProviderKind providerKind,
        string? repoProviderConfigId, string? brainProviderConfigId,
        CancellationToken ct)
    {
        // Resolve the correct provider config based on the requested kind.
        // Brain repos need their own scoped token (different repository scope).
        // Brain config lookup uses ProviderKind.Repository as storage kind — brain provider configs
        // are stored as Repository kind with RepositoryRole.Brain.
        if (providerKind == ProviderKind.Brain)
        {
            if (string.IsNullOrEmpty(brainProviderConfigId))
            {
                _logger.Warning("Brain token refresh for job {JobId}: brainProviderConfigId is null/empty. Brain sync will be disabled.", jobId);
                throw new HubException($"Brain provider config ID not available for job {jobId}. " +
                    "Brain sync cannot be performed.");
            }

            var brainConfig = await _facade.GetProviderConfigByIdAsync(brainProviderConfigId, ProviderKind.Repository, ct);
            if (brainConfig is null)
            {
                _logger.Warning("Brain token refresh for job {JobId}: config {BrainConfigId} not found in store",
                    jobId, brainProviderConfigId);
                throw new HubException($"Brain provider config '{brainProviderConfigId}' not found for job {jobId}");
            }
            return brainConfig;
        }
        else
        {
            var repoConfig = await _facade.GetProviderConfigByIdAsync(repoProviderConfigId!, ProviderKind.Repository, ct);
            if (repoConfig is null)
            {
                _logger.Warning("Provider config not found for job {JobId} (kind: {ProviderKind})", jobId, providerKind);
                throw new HubException($"Provider config not found for job {jobId} (kind: {providerKind})");
            }
            return repoConfig;
        }
    }

    private async Task<TokenRefreshResponse> VendTokenAsync(
        string jobId, ProviderKind providerKind, ProviderConfig targetConfig, CancellationToken ct)
    {
        // GitHub App auth: generate a short-lived scoped token via JWT exchange
        if (targetConfig.Settings.ContainsKey(ProviderSettingKeys.PrivateKeyBase64))
        {
            var (token, expiresAt) = await _tokenVending.GenerateAgentTokenAsync(targetConfig, ct);

            _logger.Information("Token refreshed for job {JobId} (kind: {ProviderKind}), expires at {ExpiresAt}",
                jobId, providerKind, expiresAt);

            return new TokenRefreshResponse { Token = token, ExpiresAt = expiresAt };
        }

        // GitLab PAT / static token: return the access token directly (no vending needed)
        // Static tokens have no real expiry — fabricate a far-future sentinel so the
        // agent never attempts to refresh them.
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.AccessToken, out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.Information("Returning static access token for job {JobId} (kind: {ProviderKind})",
                jobId, providerKind);

            return new TokenRefreshResponse { Token = accessToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        }

        // Fallback: check if a pre-vended token already exists in settings.
        // This path is used for GitHub App short-lived tokens written into the 'token' setting
        // at dispatch time. Read the real expiry from 'tokenExpiresAt' when available so the
        // agent can detect proximity to expiry and request a fresh token before the push.
        // Do NOT return a stale GitHub App token — throw so the agent gets an auth error
        // immediately (fail loud) instead of retrying with a token that will never work.
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.Token, out var existingToken)
            && !string.IsNullOrWhiteSpace(existingToken))
        {
            // Check whether the token is already expired (or will expire within the renewal buffer).
            // If so, this fallback branch cannot produce a valid token — throw so callers are forced
            // to acquire a fresh one rather than silently retrying with a stale credential.
            //
            // IMPORTANT: The TryGetValue and TryParse checks are intentionally separated.
            // If the key is present but the value is unparseable (e.g. a Unix-epoch integer,
            // empty/whitespace, or locale-formatted date), we must throw rather than fall through
            // to the "no expiry metadata" static-token branch — otherwise an already-expired
            // GitHub App token would be returned with a fabricated 24 h sentinel, silently
            // reintroducing the stale-token bug this fix was designed to eliminate. (CRITICAL fix)
            if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.TokenExpiresAt, out var expiresAtStr))
            {
                if (!DateTimeOffset.TryParse(expiresAtStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAtParsed))
                {
                    // TODO [WARNING]: The raw expiresAtStr value is included in the log. If a misconfigured
                    // provider accidentally stores a partial credential or sensitive string in the 'tokenExpiresAt'
                    // field, it will appear in the log in plaintext. Consider logging only the string length and
                    // first few characters rather than the full value. (Security review finding)
                    _logger.Warning(
                        "Pre-vended token for job {JobId} (kind: {ProviderKind}) has a malformed 'tokenExpiresAt' value '{Value}'. " +
                        "Cannot determine expiry — treating as expired to prevent stale-token use.",
                        jobId, providerKind, expiresAtStr);
                    throw new HubException(
                        $"Pre-vended token for job {jobId} (kind: {providerKind}) has a malformed 'tokenExpiresAt' value " +
                        "and cannot be validated. The agent must be re-dispatched with a valid provider configuration.");
                }

                // TODO [WARNING]: renewalBuffer is a local variable duplicating OrchestratorProxy.TokenRenewalBuffer (a static
                // readonly field). These two values must stay in sync — extract them into a shared constant, e.g. in a
                // TokenRefreshConstants class, to avoid silent skew between the agent proactive-renewal threshold and the
                // server-side expiry check.
                var renewalBuffer = TimeSpan.FromMinutes(5);
                if (expiresAtParsed - DateTimeOffset.UtcNow <= renewalBuffer)
                {
                    _logger.Warning(
                        "Pre-vended token for job {JobId} (kind: {ProviderKind}) is expired or expiring within {Buffer}. " +
                        "Cannot vend stale token — provider config has no GitHub App key to mint a fresh one. " +
                        "Ensure the provider uses 'privateKeyBase64' auth for long-running runs.",
                        jobId, providerKind, renewalBuffer);
                    throw new HubException(
                        $"Pre-vended token for job {jobId} (kind: {providerKind}) is expired or expiring imminently " +
                        "and no GitHub App key is present to mint a fresh one. The agent must be re-dispatched.");
                }

                _logger.Information("Returning pre-vended token for job {JobId} (kind: {ProviderKind}), expires at {ExpiresAt}",
                    jobId, providerKind, expiresAtParsed);

                return new TokenRefreshResponse { Token = existingToken, ExpiresAt = expiresAtParsed };
            }

            // No expiry metadata — this is a genuinely static token (e.g., a personal access
            // token stored directly in the provider config). Return it with a far-future sentinel.
            // TODO [WARNING]: This branch is also reachable for GitHub App dispatch tokens written by
            // older dispatch code that does not include 'tokenExpiresAt'. In that case, an already-stale
            // short-lived GitHub App token would be returned with a 24h far-future sentinel, silently
            // reintroducing the original bug for in-flight jobs dispatched before this fix was deployed.
            // Mitigation: check the provider type (GitHub App vs. static) when TokenExpiresAt is absent
            // and throw for GitHub App-typed providers instead of returning a sentinel.
            _logger.Information("Returning static token for job {JobId} (kind: {ProviderKind}) (no expiry metadata)",
                jobId, providerKind);

            return new TokenRefreshResponse { Token = existingToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(24) };
        }

        _logger.Warning("Provider config for job {JobId} (kind: {ProviderKind}) has no supported authentication method", jobId, providerKind);
        throw new HubException($"Provider config for job {jobId} (kind: {providerKind}) has no supported authentication method. " +
            "Expected 'privateKeyBase64' (GitHub App), 'accessToken' (GitLab PAT), or 'token'.");
    }
}
