using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Manages provider lifecycle: resolution from config, creation via factory,
/// validation, and disposal. Extracted from PipelineOrchestrationService to
/// separate provider lifecycle concerns from pipeline orchestration logic.
/// </summary>
public class PipelineProviderManager : IAsyncDisposable
{
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly Serilog.ILogger _logger;

    public PipelineProviderManager(
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);
        _configStore = configStore;
        _providerFactory = providerFactory;
        _logger = logger;
    }

    public IAgentProvider? ActiveAgentProvider { get; private set; }
    public IRepositoryProvider? ActiveRepoProvider { get; private set; }
    public IRepositoryProvider? ActiveBrainProvider { get; private set; }
    public IIssueProvider? ActiveIssueProvider { get; private set; }
    public IPipelineProvider? ActivePipelineProvider { get; private set; }

    /// <summary>
    /// Resolves a provider configuration by ID and kind from the config store.
    /// </summary>
    public async Task<ProviderConfig> ResolveProviderConfigAsync(string providerId, ProviderKind kind, CancellationToken ct)
    {
        var config = await _configStore.GetProviderConfigByIdAsync(providerId, kind, ct);
        if (config is null)
        {
            _logger.Error("Provider config {ProviderId} of kind {Kind} not found", providerId, kind);
            throw new InvalidOperationException($"Provider config '{providerId}' of kind '{kind}' not found.");
        }
        return config;
    }

    /// <summary>
    /// Creates the core providers (issue, repository, agent) from their resolved configs.
    /// Disposes any previously active providers first.
    /// </summary>
    public async Task CreateCoreProvidersAsync(
        ProviderConfig issueProviderConfig,
        ProviderConfig repoProviderConfig,
        ProviderConfig agentProviderConfig,
        CancellationToken ct)
    {
        await DisposePreviousProvidersAsync();
        ActiveIssueProvider = _providerFactory.CreateIssueProvider(issueProviderConfig);
        ActiveRepoProvider = _providerFactory.CreateRepositoryProvider(repoProviderConfig);
        ActiveAgentProvider = _providerFactory.CreateAgentProvider(agentProviderConfig);
        ActiveBrainProvider = null;
        ActivePipelineProvider = null;
    }

    /// <summary>
    /// Creates and validates the brain provider. Returns silently if resolution or validation fails.
    /// </summary>
    public async Task CreateBrainProviderAsync(string brainProviderId, CancellationToken ct)
    {
        try
        {
            var brainProviderConfig = await ResolveProviderConfigAsync(brainProviderId, ProviderKind.Repository, ct);
            ActiveBrainProvider = _providerFactory.CreateRepositoryProvider(brainProviderConfig);
            try { await ActiveBrainProvider.ValidateAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Brain provider validation failed, disabling brain sync for this run");
                if (ActiveBrainProvider is IAsyncDisposable disposable) await disposable.DisposeAsync();
                ActiveBrainProvider = null;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Failed to resolve brain provider {BrainProviderId}, continuing without brain", brainProviderId);
            ActiveBrainProvider = null;
        }
    }

    /// <summary>
    /// Creates the pipeline (CI) provider if a config is available.
    /// CI runs automatically when a Pipeline Provider is configured on the job template.
    /// Returns the provider config ID if one was created, otherwise null.
    /// </summary>
    public async Task<string?> CreatePipelineProviderAsync(
        string? pipelineProviderId, CancellationToken ct)
    {
        ProviderConfig? pipelineProviderConfig = null;
        if (!string.IsNullOrEmpty(pipelineProviderId))
            pipelineProviderConfig = await ResolveProviderConfigAsync(pipelineProviderId, ProviderKind.Pipeline, ct);
        else
        {
            var pipelineConfigs = await _configStore.LoadProviderConfigsAsync(ProviderKind.Pipeline, ct);
            if (pipelineConfigs is { Count: > 0 }) pipelineProviderConfig = pipelineConfigs[0];
        }

        if (pipelineProviderConfig is not null)
        {
            ActivePipelineProvider = await _providerFactory.CreatePipelineProviderAsync(pipelineProviderConfig, ct);
            return pipelineProviderConfig.Id;
        }

        return null;
    }

    /// <summary>
    /// Validates the repository, agent, and optionally pipeline providers.
    /// </summary>
    public async Task ValidateProvidersAsync(
        ProviderConfig repoConfig, ProviderConfig agentConfig, CancellationToken ct)
    {
        try { await ActiveRepoProvider!.ValidateAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Repository provider ({ProviderType}) validation failed", repoConfig.ProviderType);
            throw new InvalidOperationException($"Repository provider ({repoConfig.ProviderType}) validation failed: {ex.Message}", ex);
        }
        try { await ActiveAgentProvider!.ValidateAsync(ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Agent provider ({ProviderType}) validation failed", agentConfig.ProviderType);
            throw new InvalidOperationException($"Agent provider ({agentConfig.ProviderType}) validation failed: {ex.Message}", ex);
        }
        if (ActivePipelineProvider != null)
        {
            try { await ActivePipelineProvider.ValidateAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Error(ex, "Pipeline provider validation failed");
                throw new InvalidOperationException($"Pipeline provider validation failed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Disposes all active providers. Safe to call when some or all are null.
    /// </summary>
    public async Task DisposePreviousProvidersAsync()
    {
        await DisposeProviderAsync(ActiveAgentProvider, "Agent");
        await DisposeProviderAsync(ActiveIssueProvider, "Issue");
        await DisposeProviderAsync(ActiveRepoProvider, "Repository");
        await DisposeProviderAsync(ActiveBrainProvider, "Brain");
        await DisposeProviderAsync(ActivePipelineProvider, "Pipeline");
    }

    private async Task DisposeProviderAsync(IAsyncDisposable? provider, string providerKind)
    {
        if (provider is null) return;
        try { await provider.DisposeAsync(); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to dispose previous {ProviderKind} provider", providerKind); }
    }

    public async ValueTask DisposeAsync()
    {
        await DisposePreviousProvidersAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Resets all active provider references to null without disposing.
    /// Used by test infrastructure for state isolation between tests.
    /// </summary>
    public void Reset()
    {
        ActiveAgentProvider = null;
        ActiveRepoProvider = null;
        ActiveBrainProvider = null;
        ActiveIssueProvider = null;
        ActivePipelineProvider = null;
    }
}
