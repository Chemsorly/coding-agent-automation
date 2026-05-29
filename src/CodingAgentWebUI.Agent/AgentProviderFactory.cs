using KiroCliLib.Core;
using CodingAgentWebUI.Agent.KiroCli;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline;
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
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PipelineConfiguration _pipelineConfig;
    private readonly OrchestratorProxy? _orchestratorProxy;

    public AgentProviderFactory(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        PipelineConfiguration pipelineConfig,
        OrchestratorProxy? orchestratorProxy = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(pipelineConfig);

        _orchestrator = orchestrator;
        _httpClientFactory = httpClientFactory;
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

        if (config.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            return CreateGitHubRepositoryProvider(config);

        if (config.ProviderType.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
            return CreateGitLabRepositoryProvider(config);

        throw new NotSupportedException(
            $"Unsupported repository provider type: '{config.ProviderType}'");
    }

    public IAgentProvider CreateAgentProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ProviderType.Equals("KiroCli", StringComparison.OrdinalIgnoreCase))
            return CreateKiroCliAgentProvider(config);

        if (config.ProviderType.Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase))
            return CreateOpenCodeAgentProvider(config);

        throw new NotSupportedException(
            $"Unsupported agent provider type: '{config.ProviderType}'");
    }

    public IPipelineProvider CreatePipelineProvider(ProviderConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            return CreateGitHubPipelineProvider(config);

        if (config.ProviderType.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
            return CreateGitLabPipelineProvider(config);

        throw new NotSupportedException(
            $"Unsupported pipeline provider type: '{config.ProviderType}'");
    }

    private GitHubRepositoryProvider CreateGitHubRepositoryProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, ProviderSettingKeys.ApiUrl);
        var owner = GetRequiredSetting(config, ProviderSettingKeys.Owner);
        var repo = GetRequiredSetting(config, ProviderSettingKeys.Repo);
        var baseBranch = GetRequiredSetting(config, ProviderSettingKeys.BaseBranch);
        var connection = new GitHubConnectionInfo(apiUrl, owner, repo);

        if (_orchestratorProxy is not null)
        {
            // Use the correct ProviderKind based on the config's role so the orchestrator
            // generates a token scoped to the correct repository.
            var kind = config.RepositoryRole == RepositoryRole.Brain
                ? ProviderKind.Brain
                : ProviderKind.Repository;

            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(kind, ct);
            return new GitHubRepositoryProvider(connection, tokenProvider, baseBranch);
        }

        var token = GetRequiredSetting(config, ProviderSettingKeys.Token);
        return new GitHubRepositoryProvider(connection, token, baseBranch);
    }

    private KiroCliAgentProvider CreateKiroCliAgentProvider(ProviderConfig config)
    {
        var model = config.Settings.GetValueOrDefault(ProviderSettingKeys.Model);
        var executablePath = config.Settings.GetValueOrDefault(ProviderSettingKeys.ExecutablePath, AgentDefaults.KiroCliPath);
        return new KiroCliAgentProvider(_orchestrator, Serilog.Log.Logger, model, executablePath);
    }

    private OpenCodeAgentProvider CreateOpenCodeAgentProvider(ProviderConfig config)
    {
        var baseUrl = config.Settings.GetValueOrDefault(ProviderSettingKeys.BaseUrl, AgentDefaults.OpenCodeBaseUrl);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' has invalid baseUrl: '{baseUrl}'.",
                nameof(config));
        }

        // Validate password is available at construction time
        _ = Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword)
            ?? throw new InvalidOperationException("OPENCODE_SERVER_PASSWORD not set.");

        var model = config.Settings.GetValueOrDefault(ProviderSettingKeys.Model);
        return new OpenCodeAgentProvider(_httpClientFactory, Serilog.Log.Logger, model);
    }

    private GitHubActionsPipelineProvider CreateGitHubPipelineProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, ProviderSettingKeys.ApiUrl);
        var owner = GetRequiredSetting(config, ProviderSettingKeys.Owner);
        var repo = GetRequiredSetting(config, ProviderSettingKeys.Repo);
        var connection = new GitHubConnectionInfo(apiUrl, owner, repo);

        if (_orchestratorProxy is not null)
        {
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Pipeline, ct);
            return new GitHubActionsPipelineProvider(
                connection, tokenProvider, _pipelineConfig.ExternalCiPollInterval);
        }

        var token = GetRequiredSetting(config, ProviderSettingKeys.Token);
        return new GitHubActionsPipelineProvider(
            connection, token, _pipelineConfig.ExternalCiPollInterval);
    }

    private GitLabRepositoryProvider CreateGitLabRepositoryProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, ProviderSettingKeys.ApiUrl);
        var projectIdStr = GetRequiredSetting(config, ProviderSettingKeys.ProjectId);
        if (!int.TryParse(projectIdStr, out var projectId))
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' has invalid projectId: '{projectIdStr}'.",
                nameof(config));
        var baseBranch = config.Settings.TryGetValue(ProviderSettingKeys.BaseBranch, out var bb)
            && !string.IsNullOrWhiteSpace(bb) ? bb : ProviderSettingKeys.DefaultBaseBranch;

        if (_orchestratorProxy is not null)
        {
            var kind = config.RepositoryRole == RepositoryRole.Brain
                ? ProviderKind.Brain
                : ProviderKind.Repository;
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(kind, ct);
            return new GitLabRepositoryProvider(apiUrl, tokenProvider, projectId, baseBranch);
        }

        var token = GetRequiredSetting(config, ProviderSettingKeys.Token);
        return new GitLabRepositoryProvider(apiUrl, token, projectId, baseBranch);
    }

    private GitLabCiPipelineProvider CreateGitLabPipelineProvider(ProviderConfig config)
    {
        var apiUrl = GetRequiredSetting(config, ProviderSettingKeys.ApiUrl);
        var projectIdStr = GetRequiredSetting(config, ProviderSettingKeys.ProjectId);
        if (!int.TryParse(projectIdStr, out var projectId))
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' has invalid projectId: '{projectIdStr}'.",
                nameof(config));

        if (_orchestratorProxy is not null)
        {
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Pipeline, ct);
            return new GitLabCiPipelineProvider(apiUrl, tokenProvider, projectId, _pipelineConfig.ExternalCiPollInterval);
        }

        var token = GetRequiredSetting(config, ProviderSettingKeys.Token);
        return new GitLabCiPipelineProvider(apiUrl, token, projectId, _pipelineConfig.ExternalCiPollInterval);
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
