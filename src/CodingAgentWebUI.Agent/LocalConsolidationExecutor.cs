using System.Diagnostics;
using CodingAgentWebUI.Agent.Executors;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Telemetry;
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
public sealed class LocalConsolidationExecutor : IConsolidationExecutor
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity(
            "ExecuteConsolidation",
            ActivityKind.Consumer,
            PipelineTelemetry.ExtractTraceContext(job.TraceContext));
        activity?.SetTag("pipeline.run_id", job.JobId);
        activity?.SetTag("pipeline.consolidation_type", job.Type.ToString());

        _logger.Information("Starting consolidation job {JobId} of type {Type}",
            job.JobId, job.Type);

        // Build a per-job resolver with a fresh OrchestratorProxy so that consolidation
        // runs (e.g. brain consolidation) can refresh their repository tokens via SignalR
        // rather than using the static dispatch-time token that expires after ~1 hour.
        // TODO: Verify that the caller always passes a distinct HubConnection per consolidation
        // job. If two jobs share the same HubConnection instance, concurrent OrchestratorProxy
        // invocations on that connection may be unsafe (SignalR hub invocations are not
        // guaranteed thread-safe for simultaneous calls from different proxies). If connection
        // sharing is possible, introduce a per-job connection or a synchronization guard.
        var proxy = new OrchestratorProxy(connection, job.JobId);
        var resolver = new ConsolidationProviderResolver(_orchestrator, _httpClientFactory, _logger, proxy);

        ConsolidationJobResult result;
        try
        {
            result = await TimeoutHelper.ExecuteWithTimeoutAsync(
                job.PipelineConfiguration.AgentTimeout, ct,
                async linkedCt => job.Type switch
                {
                    ConsolidationRunType.BrainConsolidation => await ExecuteBrainConsolidationAsync(job, resolver, linkedCt),
                    ConsolidationRunType.RefactoringDetection => await ExecuteRefactoringDetectionAsync(job, resolver, linkedCt),
                    ConsolidationRunType.HarnessSuggestions => await ExecuteHarnessSuggestionsAsync(job, resolver, linkedCt),
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
        ConsolidationJobMessage job, ConsolidationProviderResolver resolver, CancellationToken ct)
    {
        var resolution = await resolver.ResolveBrainConsolidationProvidersAsync(job, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new BrainConsolidationExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.BrainProvider, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }

    private async Task<ConsolidationJobResult> ExecuteRefactoringDetectionAsync(
        ConsolidationJobMessage job, ConsolidationProviderResolver resolver, CancellationToken ct)
    {
        var resolution = await resolver.ResolveRefactoringProvidersAsync(job, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new RefactoringExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.RepoProvider, providers.BrainProvider,
            providers.IssueProvider, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }

    private async Task<ConsolidationJobResult> ExecuteHarnessSuggestionsAsync(
        ConsolidationJobMessage job, ConsolidationProviderResolver resolver, CancellationToken ct)
    {
        var resolution = await resolver.ResolveHarnessProvidersAsync(job, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;

        var executor = new HarnessSuggestionExecutor(_logger);
        return await executor.ExecuteAsync(job, providers.AgentProvider, ct,
            line => _logger.Information("Consolidation output: {Line}", line));
    }
}
