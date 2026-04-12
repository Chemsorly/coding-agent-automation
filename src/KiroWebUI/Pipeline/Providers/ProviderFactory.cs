using KiroCliLib.Core;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

public class ProviderFactory : IProviderFactory
{
    private readonly IKiroCliOrchestrator _orchestrator;

    public ProviderFactory(IKiroCliOrchestrator orchestrator)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        _orchestrator = orchestrator;
    }

    public IIssueProvider CreateIssueProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType switch
        {
            "GitHub" => CreateGitHubIssueProvider(config),
            _ => throw new NotSupportedException(
                $"Unsupported issue provider type: '{config.ProviderType}'. Supported types: GitHub")
        };
    }

    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType switch
        {
            "GitHub" => CreateGitHubRepositoryProvider(config),
            _ => throw new NotSupportedException(
                $"Unsupported repository provider type: '{config.ProviderType}'. Supported types: GitHub")
        };
    }

    public IAgentProvider CreateAgentProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType switch
        {
            "KiroCli" => CreateKiroCliAgentProvider(config),
            _ => throw new NotSupportedException(
                $"Unsupported agent provider type: '{config.ProviderType}'. Supported types: KiroCli")
        };
    }

    private static IIssueProvider CreateGitHubIssueProvider(ProviderConfig config)
    {
        ValidateRequiredSettings(config, "apiUrl", "token", "owner", "repo");

        return new GitHubIssueProvider(
            config.Settings["apiUrl"],
            config.Settings["token"],
            config.Settings["owner"],
            config.Settings["repo"]);
    }

    private static IRepositoryProvider CreateGitHubRepositoryProvider(ProviderConfig config)
    {
        ValidateRequiredSettings(config, "apiUrl", "token", "owner", "repo", "baseBranch");

        return new GitHubRepositoryProvider(
            config.Settings["apiUrl"],
            config.Settings["token"],
            config.Settings["owner"],
            config.Settings["repo"],
            config.Settings["baseBranch"]);
    }

    private IAgentProvider CreateKiroCliAgentProvider(ProviderConfig config)
    {
        // KiroCli provider uses the injected orchestrator; no required settings keys for now
        return new KiroCliAgentProvider(_orchestrator);
    }

    private static void ValidateRequiredSettings(ProviderConfig config, params string[] requiredKeys)
    {
        var missingKeys = requiredKeys
            .Where(key => !config.Settings.ContainsKey(key) || string.IsNullOrWhiteSpace(config.Settings[key]))
            .ToList();

        if (missingKeys.Count > 0)
        {
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' (type: {config.ProviderType}) is missing required settings: {string.Join(", ", missingKeys)}",
                nameof(config));
        }
    }
}
