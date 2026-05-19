using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Executes consolidation jobs locally on the agent worker. Receives a
/// <see cref="ConsolidationJobMessage"/>, resolves provider instances from the job's
/// provider configurations, dispatches to the appropriate executor based on job type,
/// and reports the result back to the orchestrator via SignalR.
/// </summary>
/// <remarks>
/// <para>
/// This class mirrors the role of <see cref="LocalPipelineExecutor"/> for regular pipeline jobs,
/// but is simpler because consolidation runs do not have the full pipeline step sequence.
/// Each consolidation type maps to a single executor:
/// </para>
/// <list type="bullet">
///   <item><see cref="ConsolidationRunType.BrainConsolidation"/> → <see cref="BrainConsolidationExecutor"/></item>
///   <item><see cref="ConsolidationRunType.RefactoringDetection"/> → <see cref="RefactoringExecutor"/></item>
///   <item><see cref="ConsolidationRunType.HarnessSuggestions"/> → <see cref="HarnessSuggestionExecutor"/></item>
/// </list>
/// </remarks>
public sealed class LocalConsolidationExecutor
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Serilog.ILogger _logger;

    public LocalConsolidationExecutor(
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

    /// <summary>
    /// Executes a consolidation job and reports the result back to the orchestrator.
    /// </summary>
    /// <param name="job">The consolidation job message from the orchestrator.</param>
    /// <param name="connection">The SignalR hub connection for reporting results.</param>
    /// <param name="ct">Cancellation token (linked to shutdown and agent timeout).</param>
    /// <returns>The consolidation job result.</returns>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        HubConnection connection,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(connection);

        _logger.Information("Starting consolidation job {JobId} of type {Type}",
            job.JobId, job.Type);

        // Link timeout from PipelineConfiguration.AgentTimeout
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(job.PipelineConfiguration.AgentTimeout);
        var linkedCt = timeoutCts.Token;

        ConsolidationJobResult result;
        try
        {
            result = job.Type switch
            {
                ConsolidationRunType.BrainConsolidation => await ExecuteBrainConsolidationAsync(job, linkedCt),
                ConsolidationRunType.RefactoringDetection => await ExecuteRefactoringDetectionAsync(job, linkedCt),
                ConsolidationRunType.HarnessSuggestions => await ExecuteHarnessSuggestionsAsync(job, linkedCt),
                _ => new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = $"Unknown consolidation run type: {job.Type}"
                }
            };
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            _logger.Warning("Consolidation job {JobId} timed out after {Timeout}",
                job.JobId, job.PipelineConfiguration.AgentTimeout);
            result = new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = $"Consolidation run timed out after {job.PipelineConfiguration.AgentTimeout}"
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            result = new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Consolidation run was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Consolidation job {JobId} failed with unhandled error", job.JobId);
            result = new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }

        // Report result back to orchestrator
        try
        {
            await connection.InvokeAsync("ReportConsolidationComplete", result, ct);
            _logger.Information("Reported consolidation result for job {JobId}: success={Success}",
                job.JobId, result.Success);
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Failed to report consolidation result for job {JobId}", job.JobId);
        }

        return result;
    }

    private async Task<ConsolidationJobResult> ExecuteBrainConsolidationAsync(
        ConsolidationJobMessage job, CancellationToken ct)
    {
        var config = job.PipelineConfiguration;
        var providerFactory = new AgentProviderFactory(_orchestrator, _httpClientFactory, config);

        // Resolve brain provider (repository provider with Brain role)
        var brainConfig = job.ProviderConfigs.FirstOrDefault(c =>
            c.Kind == ProviderKind.Repository && c.RepositoryRole == RepositoryRole.Brain);

        if (brainConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No brain repository provider configuration found in job"
            };
        }

        // Resolve agent provider
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        if (agentConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No agent provider configuration found in job"
            };
        }

        IRepositoryProvider? brainProvider = null;
        IAgentProvider? agentProvider = null;

        try
        {
            brainProvider = providerFactory.CreateRepositoryProvider(brainConfig);
            agentProvider = providerFactory.CreateAgentProvider(agentConfig);

            await brainProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);

            var executor = new BrainConsolidationExecutor(_logger);
            return await executor.ExecuteAsync(job, brainProvider, agentProvider, ct,
                line => _logger.Information("Consolidation output: {Line}", line));
        }
        finally
        {
            if (brainProvider is IAsyncDisposable bd) await bd.DisposeAsync();
            if (agentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
        }
    }

    private async Task<ConsolidationJobResult> ExecuteRefactoringDetectionAsync(
        ConsolidationJobMessage job, CancellationToken ct)
    {
        var config = job.PipelineConfiguration;
        var providerFactory = new AgentProviderFactory(_orchestrator, _httpClientFactory, config);

        // Resolve code repo provider (repository provider with Work role)
        var repoConfig = job.ProviderConfigs.FirstOrDefault(c =>
            c.Kind == ProviderKind.Repository && c.RepositoryRole == RepositoryRole.Work);

        if (repoConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No code repository provider configuration found in job"
            };
        }

        // Resolve agent provider
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        if (agentConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No agent provider configuration found in job"
            };
        }

        // Resolve issue provider config (for creating issues)
        var issueConfig = job.ProviderConfigs.FirstOrDefault(c => c.Kind == ProviderKind.Issue);
        if (issueConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No issue provider configuration found in job"
            };
        }

        // Optionally resolve brain provider for architectural context
        var brainConfig = job.ProviderConfigs.FirstOrDefault(c =>
            c.Kind == ProviderKind.Repository && c.RepositoryRole == RepositoryRole.Brain);

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IIssueProvider? issueProvider = null;
        IRepositoryProvider? brainProvider = null;

        try
        {
            repoProvider = providerFactory.CreateRepositoryProvider(repoConfig);
            agentProvider = providerFactory.CreateAgentProvider(agentConfig);

            // For refactoring, we need an issue provider — use the factory from Infrastructure
            // The agent-side factory doesn't support IIssueProvider (issue ops go through orchestrator),
            // but for consolidation we need direct issue creation. Create via the infrastructure factory.
            issueProvider = CreateIssueProviderForConsolidation(issueConfig);

            if (brainConfig is not null)
            {
                try
                {
                    brainProvider = providerFactory.CreateRepositoryProvider(brainConfig);
                    await brainProvider.ValidateAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Brain provider validation failed for consolidation job {JobId}, continuing without it", job.JobId);
                    if (brainProvider is IAsyncDisposable bd2) await bd2.DisposeAsync();
                    brainProvider = null;
                }
            }

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);

            var executor = new RefactoringExecutor(_logger);
            return await executor.ExecuteAsync(job, repoProvider, brainProvider, issueProvider, agentProvider, ct,
                line => _logger.Information("Consolidation output: {Line}", line));
        }
        finally
        {
            if (repoProvider is IAsyncDisposable rd) await rd.DisposeAsync();
            if (agentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
            if (issueProvider is IAsyncDisposable id) await id.DisposeAsync();
            if (brainProvider is IAsyncDisposable bd) await bd.DisposeAsync();
        }
    }

    private async Task<ConsolidationJobResult> ExecuteHarnessSuggestionsAsync(
        ConsolidationJobMessage job, CancellationToken ct)
    {
        var config = job.PipelineConfiguration;
        var providerFactory = new AgentProviderFactory(_orchestrator, _httpClientFactory, config);

        // Resolve agent provider
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        if (agentConfig is null)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "No agent provider configuration found in job"
            };
        }

        IAgentProvider? agentProvider = null;

        try
        {
            agentProvider = providerFactory.CreateAgentProvider(agentConfig);
            await agentProvider.ValidateAsync(ct);

            var executor = new HarnessSuggestionExecutor(_logger);
            return await executor.ExecuteAsync(job, agentProvider, ct,
                line => _logger.Information("Consolidation output: {Line}", line));
        }
        finally
        {
            if (agentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
        }
    }

    /// <summary>
    /// Creates an issue provider for consolidation runs. Unlike regular pipeline jobs where
    /// issue operations are proxied through the orchestrator, consolidation runs need direct
    /// issue creation capability for refactoring proposals.
    /// </summary>
    private IIssueProvider CreateIssueProviderForConsolidation(ProviderConfig issueConfig)
    {
        // Use the Infrastructure layer's GitHubIssueProvider directly
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

        return new CodingAgentWebUI.Infrastructure.GitHub.GitHubIssueProvider(apiUrl, token, owner, repo);
    }
}
