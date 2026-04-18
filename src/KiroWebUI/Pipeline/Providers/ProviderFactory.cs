using KiroCliLib.Core;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Services;

namespace KiroWebUI.Pipeline.Providers;

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

    public ProviderFactory(IKiroCliOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Register built-in providers
        RegisterIssueProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, "apiUrl", "clientId", "installationId", "privateKeyBase64", "owner", "repo");
            if (!long.TryParse(config.Settings["installationId"], out var installationId))
                throw new ArgumentException(
                    $"Provider '{config.DisplayName}' (type: {config.ProviderType}) has invalid installationId: '{config.Settings["installationId"]}'. Expected a numeric value.",
                    nameof(config));
            var authService = new GitHubAppAuthService(
                config.Settings["clientId"],
                installationId,
                config.Settings["privateKeyBase64"],
                config.Settings["apiUrl"],
                Serilog.Log.Logger);
            return new GitHubIssueProvider(
                config.Settings["apiUrl"],
                authService.GetTokenAsync,
                config.Settings["owner"],
                config.Settings["repo"]);
        });

        RegisterRepositoryProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, "apiUrl", "clientId", "installationId", "privateKeyBase64", "owner", "repo", "baseBranch");
            if (!long.TryParse(config.Settings["installationId"], out var installationId))
                throw new ArgumentException(
                    $"Provider '{config.DisplayName}' (type: {config.ProviderType}) has invalid installationId: '{config.Settings["installationId"]}'. Expected a numeric value.",
                    nameof(config));
            var authService = new GitHubAppAuthService(
                config.Settings["clientId"],
                installationId,
                config.Settings["privateKeyBase64"],
                config.Settings["apiUrl"],
                Serilog.Log.Logger);
            return new GitHubRepositoryProvider(
                config.Settings["apiUrl"],
                authService.GetTokenAsync,
                config.Settings["owner"],
                config.Settings["repo"],
                config.Settings["baseBranch"]);
        });

        RegisterAgentProvider("KiroCli", _ => new KiroCliAgentProvider(orchestrator));

        RegisterPipelineProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, "apiUrl", "clientId", "installationId", "privateKeyBase64", "owner", "repo");
            if (!long.TryParse(config.Settings["installationId"], out var installationId))
                throw new ArgumentException(
                    $"Provider '{config.DisplayName}' (type: {config.ProviderType}) has invalid installationId: '{config.Settings["installationId"]}'. Expected a numeric value.",
                    nameof(config));
            var authService = new GitHubAppAuthService(
                config.Settings["clientId"],
                installationId,
                config.Settings["privateKeyBase64"],
                config.Settings["apiUrl"],
                Serilog.Log.Logger);
            var pollInterval = TimeSpan.FromSeconds(
                int.TryParse(config.Settings.GetValueOrDefault("pollIntervalSeconds", "30"), out var s) ? s : 30);
            return new GitHubActionsPipelineProvider(
                config.Settings["apiUrl"],
                authService.GetTokenAsync,
                config.Settings["owner"],
                config.Settings["repo"],
                pollInterval);
        });
    }

    public void RegisterIssueProvider(string providerType, Func<ProviderConfig, IIssueProvider> factory)
        => _issueFactories[providerType] = factory;

    public void RegisterRepositoryProvider(string providerType, Func<ProviderConfig, IRepositoryProvider> factory)
        => _repoFactories[providerType] = factory;

    public void RegisterAgentProvider(string providerType, Func<ProviderConfig, IAgentProvider> factory)
        => _agentFactories[providerType] = factory;

    public void RegisterPipelineProvider(string providerType, Func<ProviderConfig, IPipelineProvider> factory)
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
}
