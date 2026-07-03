using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Resolves and validates provider instances (repository, agent, brain, pipeline) from a
/// <see cref="JobAssignmentMessage"/>. Extracted from <see cref="LocalPipelineExecutor"/>
/// to isolate provider construction and validation from pipeline execution logic.
/// </summary>
/// <remarks>
/// Handles:
/// <list type="bullet">
///   <item>Primary repo + agent provider construction and validation</item>
///   <item>Optional brain provider with fallback on validation failure</item>
///   <item>Optional pipeline provider</item>
///   <item>Additional repo providers for cross-repo decomposition</item>
///   <item>Cleanup on partial failure (disposes all successfully-created providers)</item>
/// </list>
/// </remarks>
internal sealed class AgentProviderResolver
{
    private readonly Serilog.ILogger _logger;

    public AgentProviderResolver(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Resolves all providers needed for a pipeline run from the job assignment.
    /// On failure, disposes any partially-created providers before re-throwing.
    /// </summary>
    public async Task<ResolvedProviders> ResolveAsync(
        JobAssignmentMessage job,
        IProviderFactory providerFactory,
        ProviderConfig repoConfig,
        ProviderConfig agentConfig,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(repoConfig);
        ArgumentNullException.ThrowIfNull(agentConfig);

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IRepositoryProvider? brainProvider = null;
        IPipelineProvider? pipelineProvider = null;
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null;

        try
        {
            repoProvider = providerFactory.CreateRepositoryProvider(repoConfig);
            agentProvider = providerFactory.CreateAgentProvider(agentConfig);

            brainProvider = await ResolveBrainProviderAsync(job, providerFactory, ct);
            pipelineProvider = await ResolvePipelineProviderAsync(job, providerFactory, ct);
            additionalRepoProviders = ResolveAdditionalRepoProviders(job, providerFactory);

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);
            if (pipelineProvider is not null)
                await pipelineProvider.ValidateAsync(ct);
        }
        catch
        {
            await DisposeAllAsync(repoProvider, agentProvider, brainProvider, pipelineProvider, additionalRepoProviders);
            throw;
        }

        return new ResolvedProviders(repoProvider, agentProvider, brainProvider, pipelineProvider, additionalRepoProviders);
    }

    private async Task<IRepositoryProvider?> ResolveBrainProviderAsync(
        JobAssignmentMessage job, IProviderFactory providerFactory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.BrainProviderConfigId))
            return null;

        var brainConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.BrainProviderConfigId);
        if (brainConfig is null)
            return null;

        IRepositoryProvider? brainProvider = null;
        try
        {
            brainProvider = providerFactory.CreateRepositoryProvider(brainConfig);
            await brainProvider.ValidateAsync(ct);
            return brainProvider;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Brain provider validation failed, disabling brain sync");
            if (brainProvider is not null) await brainProvider.DisposeAsync();
            return null;
        }
    }

    private async Task<IPipelineProvider?> ResolvePipelineProviderAsync(
        JobAssignmentMessage job, IProviderFactory providerFactory, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(job.PipelineProviderConfigId))
            return null;

        var pipelineConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.PipelineProviderConfigId);
        if (pipelineConfig is null)
            return null;

        return await providerFactory.CreatePipelineProviderAsync(pipelineConfig, ct);
    }

    private List<(string TemplateName, IRepositoryProvider Provider)>? ResolveAdditionalRepoProviders(
        JobAssignmentMessage job, IProviderFactory providerFactory)
    {
        if (job.ProjectContext is null ||
            job.RunType is not (PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition))
            return null;

        var additionalProviders = new List<(string TemplateName, IRepositoryProvider Provider)>();

        foreach (var repoTarget in job.ProjectContext.Repositories)
        {
            if (string.IsNullOrEmpty(repoTarget.RepoProviderId) ||
                repoTarget.RepoProviderId == job.RepoProviderConfigId)
                continue;

            var additionalConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == repoTarget.RepoProviderId);
            if (additionalConfig is null)
            {
                _logger.Warning("Additional repo provider config '{ProviderId}' for template '{Template}' not found in job assignment",
                    repoTarget.RepoProviderId, repoTarget.TemplateName);
                continue;
            }

            try
            {
                var additionalProvider = providerFactory.CreateRepositoryProvider(additionalConfig);
                additionalProviders.Add((repoTarget.TemplateName, additionalProvider));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Failed to create repo provider for template '{Template}', skipping",
                    repoTarget.TemplateName);
            }
        }

        return additionalProviders.Count > 0 ? additionalProviders : null;
    }

    private static async Task DisposeAllAsync(
        IRepositoryProvider? repoProvider,
        IAgentProvider? agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders)
    {
        if (repoProvider is not null)
            try { await repoProvider.DisposeAsync(); } catch { /* best-effort cleanup */ }
        if (agentProvider is not null)
            try { await agentProvider.DisposeAsync(); } catch { /* best-effort cleanup */ }
        if (brainProvider is not null)
            try { await brainProvider.DisposeAsync(); } catch { /* best-effort cleanup */ }
        if (pipelineProvider is not null)
            try { await pipelineProvider.DisposeAsync(); } catch { /* best-effort cleanup */ }
        if (additionalRepoProviders is not null)
        {
            foreach (var (_, provider) in additionalRepoProviders)
                try { await provider.DisposeAsync(); } catch { /* best-effort cleanup */ }
        }
    }
}

/// <summary>
/// Result of provider resolution — all providers needed for a pipeline run.
/// </summary>
internal sealed record ResolvedProviders(
    IRepositoryProvider RepoProvider,
    IAgentProvider AgentProvider,
    IRepositoryProvider? BrainProvider,
    IPipelineProvider? PipelineProvider,
    List<(string TemplateName, IRepositoryProvider Provider)>? AdditionalRepoProviders);
