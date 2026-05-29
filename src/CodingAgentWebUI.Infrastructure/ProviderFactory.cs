using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure;

/// <summary>
/// Registration-based provider factory. New provider types can be added via
/// RegisterIssueProvider/RegisterRepositoryProvider/RegisterAgentProvider/RegisterPipelineProvider
/// without modifying this class.
/// </summary>
public class ProviderFactory : IProviderFactory
{
    private readonly Dictionary<string, Func<ProviderConfig, IIssueProvider>> _issueFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ProviderConfig, IRepositoryProvider>> _repoFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ProviderConfig, IAgentProvider>> _agentFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ProviderConfig, IPipelineProvider>> _pipelineFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, GitHubAppAuthService> _authServiceCache = new(StringComparer.Ordinal);
    private readonly PipelineConfiguration _pipelineConfig;

    public ProviderFactory(PipelineConfiguration pipelineConfig)
    {
        ArgumentNullException.ThrowIfNull(pipelineConfig);

        _pipelineConfig = pipelineConfig;

        // Register built-in providers
        RegisterIssueProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, ProviderSettingKeys.ApiUrl, ProviderSettingKeys.ClientId, ProviderSettingKeys.InstallationId, ProviderSettingKeys.PrivateKeyBase64, ProviderSettingKeys.Owner, ProviderSettingKeys.Repo);
            var authService = GetOrCreateAuthService(config);
            var connection = new GitHubConnectionInfo(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.Owner],
                config.Settings[ProviderSettingKeys.Repo]);
            return new GitHubIssueProvider(connection, authService.GetTokenAsync);
        });

        RegisterRepositoryProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, ProviderSettingKeys.ApiUrl, ProviderSettingKeys.ClientId, ProviderSettingKeys.InstallationId, ProviderSettingKeys.PrivateKeyBase64, ProviderSettingKeys.Owner, ProviderSettingKeys.Repo, ProviderSettingKeys.BaseBranch);
            var authService = GetOrCreateAuthService(config);
            var connection = new GitHubConnectionInfo(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.Owner],
                config.Settings[ProviderSettingKeys.Repo]);
            return new GitHubRepositoryProvider(connection, authService.GetTokenAsync, config.Settings[ProviderSettingKeys.BaseBranch]);
        });

        RegisterPipelineProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, ProviderSettingKeys.ApiUrl, ProviderSettingKeys.ClientId, ProviderSettingKeys.InstallationId, ProviderSettingKeys.PrivateKeyBase64, ProviderSettingKeys.Owner, ProviderSettingKeys.Repo);
            var authService = GetOrCreateAuthService(config);
            var connection = new GitHubConnectionInfo(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.Owner],
                config.Settings[ProviderSettingKeys.Repo]);
            return new GitHubActionsPipelineProvider(connection, authService.GetTokenAsync, _pipelineConfig.ExternalCiPollInterval);
        });

        // Register GitLab providers
        RegisterIssueProvider("GitLab", config =>
        {
            ValidateRequiredSettings(config,
                ProviderSettingKeys.ApiUrl,
                ProviderSettingKeys.AccessToken,
                ProviderSettingKeys.ProjectId);
            var projectId = ParseProjectId(config);
            return new GitLabIssueProvider(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.AccessToken],
                projectId);
        });

        RegisterRepositoryProvider("GitLab", config =>
        {
            ValidateRequiredSettings(config,
                ProviderSettingKeys.ApiUrl,
                ProviderSettingKeys.AccessToken,
                ProviderSettingKeys.ProjectId);
            var projectId = ParseProjectId(config);
            var baseBranch = config.Settings.TryGetValue(ProviderSettingKeys.BaseBranch, out var bb)
                && !string.IsNullOrWhiteSpace(bb) ? bb : ProviderSettingKeys.DefaultBaseBranch;
            return new GitLabRepositoryProvider(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.AccessToken],
                projectId,
                baseBranch);
        });

        RegisterPipelineProvider("GitLab", config =>
        {
            ValidateRequiredSettings(config,
                ProviderSettingKeys.ApiUrl,
                ProviderSettingKeys.AccessToken,
                ProviderSettingKeys.ProjectId);
            var projectId = ParseProjectId(config);
            return new GitLabCiPipelineProvider(
                config.Settings[ProviderSettingKeys.ApiUrl],
                config.Settings[ProviderSettingKeys.AccessToken],
                projectId,
                _pipelineConfig.ExternalCiPollInterval);
        });
    }

    private void RegisterIssueProvider(string providerType, Func<ProviderConfig, IIssueProvider> factory)
        => _issueFactories[providerType] = factory;

    private void RegisterRepositoryProvider(string providerType, Func<ProviderConfig, IRepositoryProvider> factory)
        => _repoFactories[providerType] = factory;

    private void RegisterAgentProvider(string providerType, Func<ProviderConfig, IAgentProvider> factory)
        => _agentFactories[providerType] = factory;

    private void RegisterPipelineProvider(string providerType, Func<ProviderConfig, IPipelineProvider> factory)
        => _pipelineFactories[providerType] = factory;

    public IIssueProvider CreateIssueProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_issueFactories.TryGetValue(config.ProviderType, out var factory))
            return factory(config);
        throw new NotSupportedException(
            $"Unsupported issue provider type: '{config.ProviderType}'. Supported: {string.Join(", ", _issueFactories.Keys)}");
    }

    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_repoFactories.TryGetValue(config.ProviderType, out var factory))
            return factory(config);
        throw new NotSupportedException(
            $"Unsupported repository provider type: '{config.ProviderType}'. Supported: {string.Join(", ", _repoFactories.Keys)}");
    }

    public IAgentProvider CreateAgentProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_agentFactories.TryGetValue(config.ProviderType, out var factory))
            return factory(config);
        throw new NotSupportedException(
            $"Unsupported agent provider type: '{config.ProviderType}'. Supported: {string.Join(", ", _agentFactories.Keys)}");
    }

    public IPipelineProvider CreatePipelineProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (_pipelineFactories.TryGetValue(config.ProviderType, out var factory))
            return factory(config);
        throw new NotSupportedException(
            $"Unsupported pipeline provider type: '{config.ProviderType}'. Supported: {string.Join(", ", _pipelineFactories.Keys)}");
    }

    internal static void ValidateRequiredSettings(ProviderConfig config, params string[] requiredKeys)
    {
        var missingKeys = requiredKeys
            .Where(key => !config.Settings.ContainsKey(key) || string.IsNullOrWhiteSpace(config.Settings[key]))
            .ToList();

        if (missingKeys.Count > 0)
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' (type: {config.ProviderType}) is missing required settings: {string.Join(", ", missingKeys)}",
                nameof(config));
    }

    /// <summary>
    /// Parses the <see cref="ProviderSettingKeys.ProjectId"/> setting from the config as a numeric integer.
    /// Throws <see cref="ArgumentException"/> if the value is not a valid integer.
    /// </summary>
    internal static int ParseProjectId(ProviderConfig config)
    {
        var projectIdStr = config.Settings[ProviderSettingKeys.ProjectId];
        if (!int.TryParse(projectIdStr, out var projectId))
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' (type: {config.ProviderType}) has invalid projectId: '{projectIdStr}'. Expected a numeric value.",
                nameof(config));
        return projectId;
    }

    /// <summary>
    /// Returns a cached <see cref="GitHubAppAuthService"/> for the given config's
    /// clientId + installationId composite key, creating one if it doesn't exist yet.
    /// This ensures multiple providers sharing the same GitHub App installation
    /// reuse a single auth service with a single token cache.
    /// </summary>
    internal GitHubAppAuthService GetOrCreateAuthService(ProviderConfig config)
    {
        if (!long.TryParse(config.Settings[ProviderSettingKeys.InstallationId], out var installationId))
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' (type: {config.ProviderType}) has invalid installationId: '{config.Settings[ProviderSettingKeys.InstallationId]}'. Expected a numeric value.",
                nameof(config));

        var cacheKey = $"{config.Settings[ProviderSettingKeys.ClientId]}:{installationId}";

        if (_authServiceCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var authService = new GitHubAppAuthService(
            config.Settings[ProviderSettingKeys.ClientId],
            installationId,
            config.Settings[ProviderSettingKeys.PrivateKeyBase64],
            config.Settings[ProviderSettingKeys.ApiUrl],
            Serilog.Log.Logger);

        _authServiceCache[cacheKey] = authService;
        return authService;
    }
}
