using KiroCliLib.Core;
using CodingAgent.Agent.KiroCli;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent;

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

        Serilog.Log.Error("Unsupported repository provider type: {ProviderType}", config.ProviderType);
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

        Serilog.Log.Error("Unsupported agent provider type: {ProviderType}", config.ProviderType);
        throw new NotSupportedException(
            $"Unsupported agent provider type: '{config.ProviderType}'");
    }

    public Task<IPipelineProvider> CreatePipelineProviderAsync(ProviderConfig config, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(config);

        if (config.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IPipelineProvider>(CreateGitHubPipelineProvider(config));

        if (config.ProviderType.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
            return Task.FromResult<IPipelineProvider>(CreateGitLabPipelineProvider(config));

        Serilog.Log.Error("Unsupported pipeline provider type: {ProviderType}", config.ProviderType);
        throw new NotSupportedException(
            $"Unsupported pipeline provider type: '{config.ProviderType}'");
    }

    private GitHubRepositoryProvider CreateGitHubRepositoryProvider(ProviderConfig config)
    {
        ProviderFactory.ValidateRequiredSettings(config,
            ProviderSettingKeys.ApiUrl,
            ProviderSettingKeys.Owner,
            ProviderSettingKeys.Repo,
            ProviderSettingKeys.BaseBranch);
        var apiUrl = config.Settings[ProviderSettingKeys.ApiUrl];
        var owner = config.Settings[ProviderSettingKeys.Owner];
        var repo = config.Settings[ProviderSettingKeys.Repo];
        var baseBranch = config.Settings[ProviderSettingKeys.BaseBranch];
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

        ProviderFactory.ValidateRequiredSettings(config, ProviderSettingKeys.Token);
        return new GitHubRepositoryProvider(connection, config.Settings[ProviderSettingKeys.Token], baseBranch);
    }

    private KiroCliAgentProvider CreateKiroCliAgentProvider(ProviderConfig config)
    {
        var model = config.Settings.GetValueOrDefault(ProviderSettingKeys.Model);
        var executablePath = config.Settings.GetValueOrDefault(ProviderSettingKeys.ExecutablePath, AgentDefaults.KiroCliPath);
        var effort = AgentEffortLevelExtensions.ParseEffort(config.Settings.GetValueOrDefault(ProviderSettingKeys.Effort));
        return new KiroCliAgentProvider(_orchestrator, Serilog.Log.Logger, model, executablePath, effort);
    }

    private OpenCodeAgentProvider CreateOpenCodeAgentProvider(ProviderConfig config)
    {
        var baseUrl = config.Settings.GetValueOrDefault(ProviderSettingKeys.BaseUrl, AgentDefaults.OpenCodeBaseUrl);

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            Serilog.Log.Error("Provider '{DisplayName}' has invalid baseUrl: '{BaseUrl}'", config.DisplayName, baseUrl);
            throw new ArgumentException(
                $"Provider '{config.DisplayName}' has invalid baseUrl: '{baseUrl}'.",
                nameof(config));
        }

        // Validate password is available at construction time
        var openCodePassword = Environment.GetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword);
        if (openCodePassword is null)
        {
            Serilog.Log.Error("OPENCODE_SERVER_PASSWORD not set");
            throw new InvalidOperationException("OPENCODE_SERVER_PASSWORD not set.");
        }

        var model = config.Settings.GetValueOrDefault(ProviderSettingKeys.Model);
        return new OpenCodeAgentProvider(_httpClientFactory, Serilog.Log.Logger, model);
    }

    private GitHubActionsPipelineProvider CreateGitHubPipelineProvider(ProviderConfig config)
    {
        ProviderFactory.ValidateRequiredSettings(config,
            ProviderSettingKeys.ApiUrl,
            ProviderSettingKeys.Owner,
            ProviderSettingKeys.Repo);
        var apiUrl = config.Settings[ProviderSettingKeys.ApiUrl];
        var owner = config.Settings[ProviderSettingKeys.Owner];
        var repo = config.Settings[ProviderSettingKeys.Repo];
        var connection = new GitHubConnectionInfo(apiUrl, owner, repo);

        if (_orchestratorProxy is not null)
        {
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Pipeline, ct);
            return new GitHubActionsPipelineProvider(
                connection, tokenProvider, _pipelineConfig.ExternalCiPollInterval);
        }

        ProviderFactory.ValidateRequiredSettings(config, ProviderSettingKeys.Token);
        return new GitHubActionsPipelineProvider(
            connection, config.Settings[ProviderSettingKeys.Token], _pipelineConfig.ExternalCiPollInterval);
    }

    private GitLabRepositoryProvider CreateGitLabRepositoryProvider(ProviderConfig config)
    {
        ProviderFactory.ValidateRequiredSettings(config,
            ProviderSettingKeys.ApiUrl,
            ProviderSettingKeys.ProjectId);
        var apiUrl = config.Settings[ProviderSettingKeys.ApiUrl];
        var projectId = ProviderFactory.ParseProjectId(config);
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

        ProviderFactory.ValidateRequiredSettings(config, ProviderSettingKeys.Token);
        return new GitLabRepositoryProvider(apiUrl, config.Settings[ProviderSettingKeys.Token], projectId, baseBranch);
    }

    private GitLabCiPipelineProvider CreateGitLabPipelineProvider(ProviderConfig config)
    {
        ProviderFactory.ValidateRequiredSettings(config,
            ProviderSettingKeys.ApiUrl,
            ProviderSettingKeys.ProjectId);
        var apiUrl = config.Settings[ProviderSettingKeys.ApiUrl];
        var projectId = ProviderFactory.ParseProjectId(config);

        if (_orchestratorProxy is not null)
        {
            Func<CancellationToken, Task<string>> tokenProvider =
                ct => _orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Pipeline, ct);
            return new GitLabCiPipelineProvider(apiUrl, tokenProvider, projectId, _pipelineConfig.ExternalCiPollInterval);
        }

        ProviderFactory.ValidateRequiredSettings(config, ProviderSettingKeys.Token);
        return new GitLabCiPipelineProvider(apiUrl, config.Settings[ProviderSettingKeys.Token], projectId, _pipelineConfig.ExternalCiPollInterval);
    }

}
