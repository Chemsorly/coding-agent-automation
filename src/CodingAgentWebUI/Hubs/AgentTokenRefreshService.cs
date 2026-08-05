using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Handles token refresh logic for agents. Resolves provider configurations from either
/// the in-memory PipelineRun (SignalR mode) or the WorkItem payload in DB (K8s mode),
/// then returns an appropriate token based on the auth mechanism configured.
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

        // K8s mode fallback: resolve from WorkItem payload in DB
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
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.AccessToken, out var accessToken)
            && !string.IsNullOrWhiteSpace(accessToken))
        {
            _logger.Information("Returning static access token for job {JobId} (kind: {ProviderKind})",
                jobId, providerKind);

            return new TokenRefreshResponse { Token = accessToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        // Fallback: check if a pre-vended token already exists in settings
        if (targetConfig.Settings.TryGetValue(ProviderSettingKeys.Token, out var existingToken)
            && !string.IsNullOrWhiteSpace(existingToken))
        {
            _logger.Information("Returning existing token for job {JobId} (kind: {ProviderKind})",
                jobId, providerKind);

            return new TokenRefreshResponse { Token = existingToken, ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        }

        _logger.Warning("Provider config for job {JobId} (kind: {ProviderKind}) has no supported authentication method", jobId, providerKind);
        throw new HubException($"Provider config for job {jobId} (kind: {providerKind}) has no supported authentication method. " +
            "Expected 'privateKeyBase64' (GitHub App), 'accessToken' (GitLab PAT), or 'token'.");
    }
}
