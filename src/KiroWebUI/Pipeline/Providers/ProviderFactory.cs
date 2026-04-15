using KiroCliLib.Core;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Registration-based provider factory. New provider types can be added via
/// RegisterIssueProvider/RegisterRepositoryProvider/RegisterAgentProvider
/// without modifying this class.
/// </summary>
public class ProviderFactory : IProviderFactory
{
    private readonly Dictionary<string, Func<ProviderConfig, IIssueProvider>> _issueFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ProviderConfig, IRepositoryProvider>> _repoFactories = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ProviderConfig, IAgentProvider>> _agentFactories = new(StringComparer.OrdinalIgnoreCase);

    public ProviderFactory(IKiroCliOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);

        // Register built-in providers
        RegisterIssueProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, "apiUrl", "token", "owner", "repo");
            return new GitHubIssueProvider(
                config.Settings["apiUrl"], config.Settings["token"],
                config.Settings["owner"], config.Settings["repo"]);
        });

        RegisterRepositoryProvider("GitHub", config =>
        {
            ValidateRequiredSettings(config, "apiUrl", "token", "owner", "repo", "baseBranch");
            return new GitHubRepositoryProvider(
                config.Settings["apiUrl"], config.Settings["token"],
                config.Settings["owner"], config.Settings["repo"],
                config.Settings["baseBranch"]);
        });

        RegisterAgentProvider("KiroCli", _ => new KiroCliAgentProvider(orchestrator));
    }

    public void RegisterIssueProvider(string providerType, Func<ProviderConfig, IIssueProvider> factory)
        => _issueFactories[providerType] = factory;

    public void RegisterRepositoryProvider(string providerType, Func<ProviderConfig, IRepositoryProvider> factory)
        => _repoFactories[providerType] = factory;

    public void RegisterAgentProvider(string providerType, Func<ProviderConfig, IAgentProvider> factory)
        => _agentFactories[providerType] = factory;

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

    private static void ValidateRequiredSettings(ProviderConfig config, params string[] requiredKeys)
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
