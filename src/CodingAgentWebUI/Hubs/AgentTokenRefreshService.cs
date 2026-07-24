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
        // Resolve provider config IDs — from PipelineRun (SignalR mode) or WorkItem payload (K8s mode)
        string? repoProviderConfigId;
        string? brainProviderConfigId;

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            repoProviderConfigId = run.RepoProviderConfigId;
            brainProviderConfigId = run.BrainProviderConfigId;
        }
        else
        {
            // K8s mode fallback: resolve from WorkItem payload in DB
            var configIds = await _facade.GetWorkItemProviderConfigIdsAsync(jobId, ct);
            if (configIds is null)
            {
                _logger.Warning("No active run or work item found for job {JobId}", jobId);
                throw new HubException($"No active run or work item found for job {jobId}");
            }

            repoProviderConfigId = configIds.Value.RepoProviderConfigId;
            brainProviderConfigId = configIds.Value.BrainProviderConfigId;

            if (string.IsNullOrEmpty(repoProviderConfigId))
            {
                _logger.Warning("WorkItem {JobId} has no repoProviderConfigId in payload", jobId);
                throw new HubException($"WorkItem {jobId} has no repoProviderConfigId in payload");
            }
        }

        // Resolve the correct provider config based on the requested kind.
        // Brain repos need their own scoped token (different repository scope).
        ProviderConfig? targetConfig = null;

        if (providerKind == ProviderKind.Brain && !string.IsNullOrEmpty(brainProviderConfigId))
        {
            targetConfig = await _facade.GetProviderConfigByIdAsync(brainProviderConfigId, ProviderKind.Repository, ct);
        }

        if (targetConfig is null)
        {
            // Default: use the work repo config (covers Repository kind and fallback)
            targetConfig = await _facade.GetProviderConfigByIdAsync(repoProviderConfigId!, ProviderKind.Repository, ct);
        }

        if (targetConfig is null)
        {
            _logger.Warning("Provider config not found for job {JobId} (kind: {ProviderKind})", jobId, providerKind);
            throw new HubException($"Provider config not found for job {jobId} (kind: {providerKind})");
        }

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

            // Use a far-future expiry since PATs don't expire through this mechanism
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
