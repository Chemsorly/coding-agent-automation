using KiroCliLib.Core;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Agent-side provider factory that constructs providers from in-memory <see cref="ProviderConfig"/>
/// objects received in the <see cref="JobAssignmentMessage"/>. Uses short-lived tokens (the
/// <c>token</c> setting) instead of <c>privateKeyBase64</c> for GitHub API authentication.
/// <para>
/// This factory does NOT create <see cref="IIssueProvider"/> — all issue operations go through
/// <see cref="OrchestratorProxy"/> via SignalR.
/// </para>
/// </summary>
public sealed class AgentProviderFactory : IProviderFactory
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly PipelineConfiguration _pipelineConfig;
    private readonly OrchestratorProxy? _orchestratorProxy;

    public AgentProviderFactory(
        IKiroCliOrchestrator orchestrator,
        PipelineConfiguration pipelineConfig,
        OrchestratorProxy? orchestratorProxy = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(pipelineConfig);

        _orchestrator = orchestrator;
        _pipelineConfig = pipelineConfig;
        _orchestratorProxy = orchestratorProxy;
    }

    /// <summary>
    /// Not supported on the agent. All issue operations go through <see cref="OrchestratorProxy"/>.
    /// </summary>
    public IIssueProvider CreateIssueProvider(ProviderConfig config)
        => throw new NotSupportedException(
            "Agent workers do not create IIssueProvider instances. " +
            "All issue operations are proxied through the orchestrator via SignalR.");

    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase)
            ? CreateGitHubRepositoryProvider(config)
            : throw new NotSupportedException(
                $"Unsupported repository provider type: '{config.ProviderType}'");
    }

    public IAgentProvider CreateAgentProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType.Equals("KiroCli", StringComparison.OrdinalIgnoreCase)
            ? CreateKiroCliAgentProvider(config)
            : throw new NotSupportedException(
                $"Unsupported agent provider type: '{config.ProviderType}'");
    }

    public IPipelineProvider CreatePipelineProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return config.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase)
            ? CreateGitHubPipelineProvider(config)
            : throw new NotSupportedException(
                $"Unsupported pipeline provider type: '{config.ProviderType}'");
    }

    private GitHubRepositoryProvider CreateGitHubRepositoryProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, "apiUrl");
        var owner = GetRequiredSetting(config, "owner");
        var repo = GetRequiredSetting(config, "repo");
        var baseBranch = GetRequiredSetting(config, "baseBranch");

        if (_orchestratorProxy is not null)
        {
            // Use the token provider constructor — refreshes token via orchestrator before each operation
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Repository, ct);
            return new GitHubRepositoryProvider(apiUrl, tokenProvider, owner, repo, baseBranch);
        }

        var token = GetRequiredSetting(config, "token");
        return new GitHubRepositoryProvider(apiUrl, token, owner, repo, baseBranch);
    }

    private KiroCliAgentProvider CreateKiroCliAgentProvider(ProviderConfig config)
    {
        var model = config.Settings.GetValueOrDefault("model");
        var executablePath = config.Settings.GetValueOrDefault("executablePath", "/home/ubuntu/.local/bin/kiro-cli");
        return new KiroCliAgentProvider(_orchestrator, Serilog.Log.Logger, model, executablePath);
    }

    private GitHubActionsPipelineProvider CreateGitHubPipelineProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, "apiUrl");
        var owner = GetRequiredSetting(config, "owner");
        var repo = GetRequiredSetting(config, "repo");

        if (_orchestratorProxy is not null)
        {
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Pipeline, ct);
            return new GitHubActionsPipelineProvider(
                apiUrl, tokenProvider, owner, repo, _pipelineConfig.ExternalCiPollInterval);
        }

        var token = GetRequiredSetting(config, "token");
        return new GitHubActionsPipelineProvider(
            apiUrl, token, owner, repo, _pipelineConfig.ExternalCiPollInterval);
    }

    private static string GetRequiredSetting(ProviderConfig config, string key)
    {
        if (config.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            return value;

        throw new ArgumentException(
            $"Provider '{config.DisplayName}' (type: {config.ProviderType}) is missing required setting: '{key}'",
            nameof(config));
    }
}
