using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Resolves provider instances for consolidation jobs. Encapsulates the pattern of
/// looking up <see cref="ProviderConfig"/> entries, null-checking, creating providers
/// via <see cref="AgentProviderFactory"/>, validating, and wrapping them in a
/// disposable context.
/// </summary>
internal sealed class ConsolidationProviderResolver
{
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
        ConsolidationJobMessage job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<BrainConsolidationProviders>(job, async (factory, disposables) =>
        {
            var brainConfig = FindRequiredConfig(job, ProviderKind.Repository, RepositoryRole.Brain);
            if (brainConfig is null)
                return ProviderResolutionResult<BrainConsolidationProviders>.Fail(job.JobId,
                    "No brain repository provider configuration found in job");

            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<BrainConsolidationProviders>.Fail(job.JobId,
                    "No agent provider configuration found in job");

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
        ConsolidationJobMessage job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<RefactoringProviders>(job, async (factory, disposables) =>
        {
            var repoConfig = FindRequiredConfig(job, ProviderKind.Repository, RepositoryRole.Work);
            if (repoConfig is null)
                return ProviderResolutionResult<RefactoringProviders>.Fail(job.JobId,
                    "No code repository provider configuration found in job");

            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<RefactoringProviders>.Fail(job.JobId,
                    "No agent provider configuration found in job");

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

            var issueProvider = CreateIssueProviderForConsolidation(issueConfig);
            if (issueProvider is IAsyncDisposable id) disposables.Add(id);

            IRepositoryProvider? brainProvider = null;
            if (brainConfig is not null)
            {
                try
                {
                    brainProvider = factory.CreateRepositoryProvider(brainConfig);
                    if (brainProvider is IAsyncDisposable bd) disposables.Add(bd);
                    await brainProvider.ValidateAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Brain provider validation failed for consolidation job {JobId}, continuing without it", job.JobId);
                    if (brainProvider is IAsyncDisposable bd2)
                    {
                        await bd2.DisposeAsync();
                        disposables.Remove(bd2);
                    }
                    brainProvider = null;
                }
            }

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);

            return ProviderResolutionResult<RefactoringProviders>.Succeed(
                new RefactoringProviders(repoProvider, agentProvider, issueProvider, brainProvider));
        }, ct);
    }

    public Task<ProviderResolutionResult<HarnessProviders>> ResolveHarnessProvidersAsync(
        ConsolidationJobMessage job, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        return ResolveAsync<HarnessProviders>(job, async (factory, disposables) =>
        {
            var agentConfig = FindRequiredConfig(job, ProviderKind.Agent);
            if (agentConfig is null)
                return ProviderResolutionResult<HarnessProviders>.Fail(job.JobId,
                    "No agent provider configuration found in job");

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
        Func<AgentProviderFactory, List<IAsyncDisposable>, Task<ProviderResolutionResult<T>>> resolver,
        CancellationToken ct) where T : IAsyncDisposable
    {
        var factory = new AgentProviderFactory(_orchestrator, _httpClientFactory, job.PipelineConfiguration);
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
    /// </summary>
    private static IIssueProvider CreateIssueProviderForConsolidation(ProviderConfig issueConfig)
    {
        var apiUrl = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.ApiUrl, "https://api.github.com");
        var token = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Token)
            ?? throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'token' setting for consolidation");
        var owner = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Owner)
            ?? throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'owner' setting for consolidation");
        var repo = issueConfig.Settings.GetValueOrDefault(ProviderSettingKeys.Repo)
            ?? throw new InvalidOperationException(
                $"Issue provider '{issueConfig.DisplayName}' is missing 'repo' setting for consolidation");

        var connection = new GitHubConnectionInfo(apiUrl, owner, repo);
        return new GitHubIssueProvider(connection, token);
    }
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

    public async ValueTask DisposeAsync()
    {
        if (BrainProvider is IAsyncDisposable bd) await bd.DisposeAsync();
        if (AgentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
    }
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

    public async ValueTask DisposeAsync()
    {
        if (RepoProvider is IAsyncDisposable rd) await rd.DisposeAsync();
        if (AgentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
        if (IssueProvider is IAsyncDisposable id) await id.DisposeAsync();
        if (BrainProvider is IAsyncDisposable bd) await bd.DisposeAsync();
    }
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

    public async ValueTask DisposeAsync()
    {
        if (AgentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
    }
}
