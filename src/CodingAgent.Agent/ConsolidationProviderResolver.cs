using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.GitHub;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgent.Agent;

/// <summary>
/// Resolves provider instances for consolidation jobs. Encapsulates the pattern of
/// looking up <see cref="ProviderConfig"/> entries, null-checking, creating providers
/// via <see cref="AgentProviderFactory"/>, validating, and wrapping them in a
/// disposable context.
/// </summary>
internal sealed class ConsolidationProviderResolver
{
    private const string NoAgentProviderConfigMessage = "No agent provider configuration found in job";

    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Serilog.ILogger _logger;

    public ConsolidationProviderResolver(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public Task<ProviderResolutionResult<BrainConsolidationProviders>> ResolveBrainConsolidationProvidersAsync(
        ConsolidationJobMessage job, CancellationToken ct) =>
        ResolveBrainConsolidationProvidersAsync(job, null, ct);

    public Task<ProviderResolutionResult<BrainConsolidationProviders>> ResolveBrainConsolidationProvidersAsync(
        ConsolidationJobMessage job, OrchestratorProxy? orchestratorProxy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<BrainConsolidationProviders>(job, orchestratorProxy, async (factory, disposables) =>
        {
            var brainConfig = FindRequiredConfig(job, ProviderKind.Repository, RepositoryRole.Brain);
            if (brainConfig is null)
                return ProviderResolutionResult<BrainConsolidationProviders>.Fail(job.JobId,
                    "No brain repository provider configuration found in job");

            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<BrainConsolidationProviders>.Fail(job.JobId,
                    NoAgentProviderConfigMessage);

            var brainProvider = factory.CreateRepositoryProvider(brainConfig);
            if (brainProvider is IAsyncDisposable bd) disposables.Add(bd);

            var agentProvider = factory.CreateAgentProvider(agentConfig);
            if (agentProvider is IAsyncDisposable ad) disposables.Add(ad);

            await brainProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);

            return ProviderResolutionResult<BrainConsolidationProviders>.Succeed(
                new BrainConsolidationProviders(brainProvider, agentProvider));
        }, ct);
    }

    public Task<ProviderResolutionResult<RefactoringProviders>> ResolveRefactoringProvidersAsync(
        ConsolidationJobMessage job, CancellationToken ct) =>
        ResolveRefactoringProvidersAsync(job, null, ct);

    public Task<ProviderResolutionResult<RefactoringProviders>> ResolveRefactoringProvidersAsync(
        ConsolidationJobMessage job, OrchestratorProxy? orchestratorProxy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<RefactoringProviders>(job, orchestratorProxy, async (factory, disposables) =>
        {
            var repoConfig = FindRequiredConfig(job, ProviderKind.Repository, RepositoryRole.Work);
            if (repoConfig is null)
                return ProviderResolutionResult<RefactoringProviders>.Fail(job.JobId,
                    "No code repository provider configuration found in job");

            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<RefactoringProviders>.Fail(job.JobId,
                    NoAgentProviderConfigMessage);

            var issueConfig = FindRequiredConfig(job, ProviderKind.Issue);
            if (issueConfig is null)
                return ProviderResolutionResult<RefactoringProviders>.Fail(job.JobId,
                    "No issue provider configuration found in job");

            var brainConfig = job.ProviderConfigs.FirstOrDefault(c =>
                c.Kind == ProviderKind.Repository && c.RepositoryRole == RepositoryRole.Brain);

            var repoProvider = factory.CreateRepositoryProvider(repoConfig);
            if (repoProvider is IAsyncDisposable rd) disposables.Add(rd);

            var agentProvider = factory.CreateAgentProvider(agentConfig);
            if (agentProvider is IAsyncDisposable ad) disposables.Add(ad);

            var issueProvider = CreateIssueProviderForConsolidation(issueConfig, orchestratorProxy);
            if (issueProvider is IAsyncDisposable id) disposables.Add(id);

            var brainProvider = brainConfig is not null
                ? await TryCreateAndValidateBrainProviderAsync(factory, brainConfig, disposables, job.JobId, ct)
                : null;

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);
            await issueProvider.ValidateAsync(ct);

            return ProviderResolutionResult<RefactoringProviders>.Succeed(
                new RefactoringProviders(repoProvider, agentProvider, issueProvider, brainProvider));
        }, ct);
    }

    /// <summary>
    /// Creates and validates a brain repository provider, registering it in <paramref name="disposables"/>.
    /// Returns null (and removes from disposables) if validation fails, so the job can proceed without it.
    /// </summary>
    private async Task<IRepositoryProvider?> TryCreateAndValidateBrainProviderAsync(
        AgentProviderFactory factory,
        ProviderConfig brainConfig,
        List<IAsyncDisposable> disposables,
        string jobId,
        CancellationToken ct)
    {
        IRepositoryProvider? brainProvider = null;
        try
        {
            brainProvider = factory.CreateRepositoryProvider(brainConfig);
            if (brainProvider is IAsyncDisposable bd) disposables.Add(bd);
            await brainProvider.ValidateAsync(ct);
            return brainProvider;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Brain provider validation failed for consolidation job {JobId}, continuing without it", jobId);
            if (brainProvider is IAsyncDisposable bd2)
            {
                await bd2.DisposeAsync();
                disposables.Remove(bd2);
            }
            return null;
        }
    }

    public Task<ProviderResolutionResult<HarnessProviders>> ResolveHarnessProvidersAsync(
        ConsolidationJobMessage job, CancellationToken ct) =>
        ResolveHarnessProvidersAsync(job, null, ct);

    public Task<ProviderResolutionResult<HarnessProviders>> ResolveHarnessProvidersAsync(
        ConsolidationJobMessage job, OrchestratorProxy? orchestratorProxy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<HarnessProviders>(job, orchestratorProxy, async (factory, disposables) =>
        {
            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<HarnessProviders>.Fail(job.JobId,
                    NoAgentProviderConfigMessage);

            var agentProvider = factory.CreateAgentProvider(agentConfig);
            if (agentProvider is IAsyncDisposable ad) disposables.Add(ad);

            await agentProvider.ValidateAsync(ct);

            return ProviderResolutionResult<HarnessProviders>.Succeed(new HarnessProviders(agentProvider));
        }, ct);
    }

    /// <summary>
    /// Generic helper that encapsulates factory creation and dispose-on-failure cleanup.
    /// The resolver delegate registers created providers in the disposables list; on success
    /// the list is cleared (ownership transferred to the holder), on failure all are disposed.
    /// </summary>
    private async Task<ProviderResolutionResult<T>> ResolveAsync<T>(
        ConsolidationJobMessage job,
        OrchestratorProxy? orchestratorProxy,
        Func<AgentProviderFactory, List<IAsyncDisposable>, Task<ProviderResolutionResult<T>>> resolver,
        CancellationToken ct) where T : IAsyncDisposable
    {
        var factory = new AgentProviderFactory(_orchestrator, _httpClientFactory, job.PipelineConfiguration, orchestratorProxy);
        var disposables = new List<IAsyncDisposable>();
        try
        {
            var result = await resolver(factory, disposables);
            if (result.IsSuccess)
                disposables.Clear();
            return result;
        }
        finally
        {
            foreach (var d in disposables)
                await d.DisposeAsync();
        }
    }

    /// <summary>
    /// Finds a required provider config by kind and optional repository role.
    /// Returns null when not found — caller is responsible for returning a failure result.
    /// </summary>
    private static ProviderConfig? FindRequiredConfig(
        ConsolidationJobMessage job, ProviderKind kind, RepositoryRole? role = null) =>
        job.ProviderConfigs.FirstOrDefault(c =>
            c.Kind == kind && (role is null || c.RepositoryRole == role));

    /// <summary>
    /// Creates an issue provider for consolidation runs. Unlike regular pipeline jobs where
    /// issue operations are proxied through the orchestrator, consolidation runs need direct
    /// issue creation capability for refactoring proposals.
    /// When <paramref name="orchestratorProxy"/> is non-null, GitHub providers use the proxy's
    /// token-refresh delegate so that long-running jobs survive GitHub App token expiry (~1 hr).
    /// </summary>
    private static IIssueProvider CreateIssueProviderForConsolidation(
        ProviderConfig issueConfig, OrchestratorProxy? orchestratorProxy)
    {
        if (issueConfig.ProviderType.Equals("GitHub", StringComparison.OrdinalIgnoreCase))
            return CreateGitHubIssueProvider(issueConfig, orchestratorProxy);

        if (issueConfig.ProviderType.Equals("GitLab", StringComparison.OrdinalIgnoreCase))
            return CreateGitLabIssueProvider(issueConfig);

        Serilog.Log.Error("Unsupported issue provider type for consolidation: '{ProviderType}'", issueConfig.ProviderType);
        throw new InvalidOperationException(
            $"Unsupported issue provider type for consolidation: '{issueConfig.ProviderType}'");
    }

    private static GitHubIssueProvider CreateGitHubIssueProvider(
        ProviderConfig issueConfig, OrchestratorProxy? orchestratorProxy)
    {
        var apiUrl = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.ApiUrl, "https://api.github.com");

        // owner and repo are required unconditionally — they are needed to construct GitHubConnectionInfo
        // regardless of whether a proxy (refresh delegate) or a static token is used.
        var owner = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Owner);
        if (owner is null)
        {
            Serilog.Log.Error("Issue provider '{DisplayName}' is missing 'owner' setting for consolidation", issueConfig.DisplayName);
            throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'owner' setting for consolidation");
        }
        var repo = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Repo);
        if (repo is null)
        {
            Serilog.Log.Error("Issue provider '{DisplayName}' is missing 'repo' setting for consolidation", issueConfig.DisplayName);
            throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'repo' setting for consolidation");
        }

        var connection = new GitHubConnectionInfo(apiUrl, owner, repo);

        // When a proxy is available, use the refresh-capable constructor so the provider
        // automatically obtains a fresh token after expiry. GitHub App installation tokens
        // are valid for ~1 hour; RefactoringDetection jobs can exceed this when AgentTimeout
        // is high. ProviderKind.Repository is correct because VendProviderConfigsAsync sets
        // includeIssuePermission = true for RefactoringDetection, so the repo token already
        // carries issues:write scope — no separate ProviderKind.Issue path exists.
        //
        // NOTE: owner and repo null-checks above are intentionally evaluated BEFORE this branch.
        // The proxy path returns early here and skips the token null-check below, but
        // GitHubConnectionInfo construction still requires non-null owner and repo. Any future
        // refactor that moves this branch earlier (before the owner/repo guards) would construct
        // GitHubConnectionInfo with null values. Keep the owner/repo guards above this branch.
        // TODO [WARNING]: The lambda captures orchestratorProxy by reference. OrchestratorProxy
        // is IDisposable and is owned by LocalConsolidationExecutor, which disposes it after the
        // consolidation run completes. If issue-creation retries in RefactoringExecutor.CreateIssuesAsync
        // outlive the proxy's disposal, the delegate will invoke a disposed object. This widens the
        // same risk that already exists on the repo provider closure. Consider passing a scoped refresh
        // func with a clear lifetime boundary rather than closing over the proxy directly.
        // (Correctness / DotNetSpecialist)
        // TODO [WARNING]: includeIssuePermission: true is only honored for GitHub App-backed repo
        // providers (those with privateKeyBase64). If the repo provider is PAT/static-token
        // configured (no privateKeyBase64), AgentTokenRefreshService.VendTokenAsync will silently
        // ignore the flag and return the static token unchanged — no issues:write guarantee.
        // RefactoringDetection is expected to always use a GitHub App, but if it is ever enabled
        // for a PAT-only repo provider the 403 will still occur after token expiry.
        // (Correctness Review)
        if (orchestratorProxy is not null)
            return new GitHubIssueProvider(connection,
                refreshCt => orchestratorProxy.RequestTokenRefreshAsync(ProviderKind.Repository, refreshCt, includeIssuePermission: true));

        // Fallback: static token path for PAT-configured providers and test scenarios where
        // no proxy is available (orchestratorProxy is null).
        var token = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Token);
        if (token is null)
        {
            Serilog.Log.Error("Issue provider '{DisplayName}' is missing 'token' setting for consolidation", issueConfig.DisplayName);
            throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'token' setting for consolidation");
        }
        return new GitHubIssueProvider(connection, token);
    }

    private static GitLabIssueProvider CreateGitLabIssueProvider(ProviderConfig issueConfig)
    {
        var apiUrl = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.ApiUrl, ProviderSettingKeys.DefaultGitLabApiUrl);

        // Validate required settings through the shared helper (throws ArgumentException with all
        // missing keys in one message, consistent with ProviderFactory and AgentProviderFactory).
        ProviderFactory.ValidateRequiredSettings(issueConfig,
            ProviderSettingKeys.AccessToken,
            ProviderSettingKeys.ProjectId);

        var accessToken = issueConfig.Settings[ProviderSettingKeys.AccessToken];

        // Parse projectId through the shared helper (throws ArgumentException, consistent with
        // ProviderFactory.ParseProjectId used by all other GitLab provider construction paths).
        var projectId = ProviderFactory.ParseProjectId(issueConfig);

        return new GitLabIssueProvider(apiUrl, accessToken, projectId);
    }

    /// <summary>
    /// Internal test-only entry point: directly constructs the <see cref="GitHubIssueProvider"/>
    /// for the given config and optional proxy, without running the full resolution pipeline.
    /// This allows unit tests to verify constructor-path selection (refresh delegate vs. static
    /// token) without depending on SignalR handshake behaviour or provider validation.
    /// </summary>
    internal static GitHubIssueProvider CreateGitHubIssueProviderForTest(
        ProviderConfig issueConfig, OrchestratorProxy? orchestratorProxy)
        => CreateGitHubIssueProvider(issueConfig, orchestratorProxy);
}

/// <summary>
/// Result of a provider resolution attempt. Contains either the resolved providers
/// or a failure result with an error message.
/// </summary>
internal sealed class ProviderResolutionResult<T> where T : IAsyncDisposable
{
    public T? Providers { get; }
    public ConsolidationJobResult? Failure { get; }
    public bool IsSuccess => Providers is not null;

    private ProviderResolutionResult(T? providers, ConsolidationJobResult? failure)
    {
        Providers = providers;
        Failure = failure;
    }

    public static ProviderResolutionResult<T> Succeed(T providers) => new(providers, null);

    public static ProviderResolutionResult<T> Fail(string jobId, string errorMessage) =>
        new(default, new ConsolidationJobResult { JobId = jobId, Success = false, ErrorMessage = errorMessage });
}

/// <summary>
/// Holds resolved providers for brain consolidation. Disposes brain then agent.
/// </summary>
internal sealed class BrainConsolidationProviders : IAsyncDisposable
{
    public IRepositoryProvider BrainProvider { get; }
    public IAgentProvider AgentProvider { get; }

    public BrainConsolidationProviders(IRepositoryProvider brainProvider, IAgentProvider agentProvider)
    {
        BrainProvider = brainProvider;
        AgentProvider = agentProvider;
    }

    public async ValueTask DisposeAsync() =>
        await ProviderDisposer.DisposeAllAsync(BrainProvider, AgentProvider);
}

/// <summary>
/// Holds resolved providers for refactoring detection. Disposes repo, agent, issue, brain.
/// </summary>
internal sealed class RefactoringProviders : IAsyncDisposable
{
    public IRepositoryProvider RepoProvider { get; }
    public IAgentProvider AgentProvider { get; }
    public IIssueProvider IssueProvider { get; }
    public IRepositoryProvider? BrainProvider { get; }

    public RefactoringProviders(
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IIssueProvider issueProvider,
        IRepositoryProvider? brainProvider)
    {
        RepoProvider = repoProvider;
        AgentProvider = agentProvider;
        IssueProvider = issueProvider;
        BrainProvider = brainProvider;
    }

    public async ValueTask DisposeAsync() =>
        await ProviderDisposer.DisposeAllAsync(RepoProvider, AgentProvider, IssueProvider, BrainProvider);
}

/// <summary>
/// Holds resolved providers for harness suggestions. Disposes agent.
/// </summary>
internal sealed class HarnessProviders : IAsyncDisposable
{
    public IAgentProvider AgentProvider { get; }

    public HarnessProviders(IAgentProvider agentProvider)
    {
        AgentProvider = agentProvider;
    }

    public async ValueTask DisposeAsync() =>
        await ProviderDisposer.DisposeAllAsync(AgentProvider);
}
