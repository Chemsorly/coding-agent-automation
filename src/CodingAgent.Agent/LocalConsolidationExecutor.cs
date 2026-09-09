using System.Diagnostics;
using CodingAgent.Agent.Executors;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgent.Agent;

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
public sealed class LocalConsolidationExecutor : IConsolidationExecutor
{
    private readonly ConsolidationProviderResolver _resolver;
    private readonly Serilog.ILogger _logger;

    public LocalConsolidationExecutor(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = new ConsolidationProviderResolver(orchestrator, httpClientFactory, logger);
        _logger = logger;
    }

    /// <summary>
    /// Executes a consolidation job and reports the result back to the orchestrator.</summary>
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity(
            "ExecuteConsolidation",
            ActivityKind.Consumer,
            PipelineTelemetry.ExtractTraceContext(job.TraceContext));
        activity?.SetTag("pipeline.run_id", job.JobId);
        activity?.SetTag("pipeline.consolidation_type", job.Type.ToString());

        _logger.Information("Starting consolidation job {JobId} of type {Type}",
            job.JobId, job.Type);

        // Create an OrchestratorProxy for the consolidation run so that the repository
        // provider uses the proactive token-refresh path instead of the static dispatch token.
        // This mirrors what LocalPipelineExecutor does for regular pipeline jobs.
        // TODO [WARNING]: OrchestratorProxy is not disposed after use. Once IDisposable is added to
        // OrchestratorProxy (to dispose _tokenCacheLock), this site will silently suppress the disposal
        // obligation. Wrap in a using block here, and do the same in LocalPipelineExecutor. (DotNetSpecialist review finding)
        var orchestratorProxy = new OrchestratorProxy(connection, job.JobId);

        ConsolidationJobResult result;
        try
        {
            result = await TimeoutHelper.ExecuteWithTimeoutAsync(
                job.PipelineConfiguration.AgentTimeout, ct,
                async linkedCt => job.Type switch
                {
                    ConsolidationRunType.BrainConsolidation => await ExecuteBrainConsolidationAsync(job, orchestratorProxy, linkedCt),
                    ConsolidationRunType.RefactoringDetection => await ExecuteRefactoringDetectionAsync(job, orchestratorProxy, linkedCt),
                    ConsolidationRunType.HarnessSuggestions => await ExecuteHarnessSuggestionsAsync(job, orchestratorProxy, linkedCt),
                    _ => new ConsolidationJobResult
                    {
                        JobId = job.JobId,
                        Success = false,
                        ErrorMessage = $"Unknown consolidation run type: {job.Type}"
                    }
                },
                () =>
                {
                    _logger.Warning("Consolidation job {JobId} timed out after {Timeout}",
                        job.JobId, job.PipelineConfiguration.AgentTimeout);
                    return Task.FromResult(new ConsolidationJobResult
                    {
                        JobId = job.JobId,
                        Success = false,
                        ErrorMessage = $"Consolidation run timed out after {job.PipelineConfiguration.AgentTimeout}"
                    });
                });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            activity?.SetTag("pipeline.cancelled", true);
            result = new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Consolidation run was cancelled"
            };
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
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
            await connection.InvokeAsync(HubMethodNames.ReportConsolidationComplete, result, ct);
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
        ConsolidationJobMessage job, OrchestratorProxy orchestratorProxy, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveBrainConsolidationProvidersAsync(job, orchestratorProxy, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new BrainConsolidationExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.BrainProvider, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }

    private async Task<ConsolidationJobResult> ExecuteRefactoringDetectionAsync(
        ConsolidationJobMessage job, OrchestratorProxy orchestratorProxy, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveRefactoringProvidersAsync(job, orchestratorProxy, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new RefactoringExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.RepoProvider, providers.BrainProvider,
            providers.IssueProvider, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }

    private async Task<ConsolidationJobResult> ExecuteHarnessSuggestionsAsync(
        ConsolidationJobMessage job, OrchestratorProxy orchestratorProxy, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveHarnessProvidersAsync(job, orchestratorProxy, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new HarnessSuggestionExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }
}
