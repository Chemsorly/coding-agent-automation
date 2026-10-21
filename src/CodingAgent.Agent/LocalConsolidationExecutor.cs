using System.Diagnostics;
using CodingAgent.Agent.Executors;
using CodingAgent.Infrastructure;
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
/// </list>
/// </remarks>
public sealed class LocalConsolidationExecutor : IConsolidationExecutor
{
    private readonly ConsolidationProviderResolver _resolver;
    private readonly Serilog.ILogger _logger;
    private readonly IBrainUpdateService _brainUpdateService;
    private readonly IPipelineReporterFactory _reporterFactory;

    public LocalConsolidationExecutor(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineReporterFactory? reporterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _resolver = new ConsolidationProviderResolver(orchestrator, httpClientFactory, logger);
        _logger = logger;
        _brainUpdateService = brainUpdateService ?? new CodingAgent.Infrastructure.Git.BrainUpdateService(logger);
        _reporterFactory = reporterFactory ?? new PipelineReporterFactory(logger);
    }

    /// <summary>
    /// Executes a consolidation job and reports the result back to the orchestrator.
    /// </summary>
    /// <param name="job">The consolidation job message from the orchestrator.</param>
    /// <param name="connection">The SignalR hub connection for reporting results.</param>
    /// <param name="ct">Cancellation token (linked to shutdown and agent timeout).</param>
    /// <returns>The consolidation job result.</returns>
    public Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        HubConnection connection,
        CancellationToken ct)
        => ExecuteAsync(job, connection, null, null, ct);

    /// <summary>
    /// Executes a consolidation job, reports its steps and streams its agent output while it runs, and
    /// reports the result back to the orchestrator. Without an <paramref name="outputBatcher"/> nothing is
    /// reported while the job runs.
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        HubConnection connection,
        OutputBatcher? outputBatcher,
        Action<PipelineStep?>? onStepChanged,
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
        using var orchestratorProxy = new OrchestratorProxy(connection, job.JobId);

        PipelineSignalRReporter? reporter = null;
        ConsolidationProgress? progress = null;
        Action<string> emitOutputLine;

        if (outputBatcher is not null)
        {
            var run = ConsolidationProgress.CreateRun(job);
            reporter = _reporterFactory.Create(connection, outputBatcher, job.JobId, run, onStepChanged);
            progress = new ConsolidationProgress(run, step => reporter.TransitionTo(step, ct));
            emitOutputLine = line => reporter.EmitOutputLine(line, null, ct);
        }
        else
        {
            emitOutputLine = line => _logger.Information("Consolidation output: {Line}", line);
        }

        ConsolidationJobResult result;
        try
        {
            result = await TimeoutHelper.ExecuteWithTimeoutAsync(
                job.PipelineConfiguration.AgentTimeout, ct,
                async linkedCt => job.Type switch
                {
                    ConsolidationRunType.BrainConsolidation => await ExecuteBrainConsolidationAsync(job, orchestratorProxy, progress, emitOutputLine, linkedCt),
                    ConsolidationRunType.RefactoringDetection => await ExecuteRefactoringDetectionAsync(job, orchestratorProxy, progress, emitOutputLine, linkedCt),
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
        finally
        {
            if (reporter is not null)
            {
                try { await reporter.DisposeAsync(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose reporter for consolidation job {JobId}", job.JobId); }
            }
        }

        // Report result back to orchestrator (after reporter is disposed, so all step transitions are drained first)
        if (progress is not null) progress.CopyUsageTo(result);
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

    /// <summary>
    /// Writes the job's MCP servers to the agent's MCP config, as <see cref="WriteMcpConfigStep"/> does for
    /// pipeline runs, so the consolidation agents can use the project's MCP tools.
    /// </summary>
    internal static void WriteMcpConfig(
        ConsolidationJobMessage job, string mcpConfigPath, Serilog.ILogger logger,
        AgentProviderType providerType = AgentProviderType.KiroCli)
    {
        if (job.McpServers is not { Count: > 0 })
            return;

        try
        {
            McpConfigWriter.WriteConfig(mcpConfigPath, job.McpServers, providerType);
            logger.Information("Wrote MCP config with {Count} server(s) for consolidation job {JobId}",
                job.McpServers.Count, job.JobId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Failed to write MCP config for consolidation job {JobId}, continuing without it", job.JobId);
        }
    }

    private async Task<ConsolidationJobResult> ExecuteBrainConsolidationAsync(
        ConsolidationJobMessage job, OrchestratorProxy orchestratorProxy,
        ConsolidationProgress? progress, Action<string> emitOutputLine, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveBrainConsolidationProvidersAsync(job, orchestratorProxy, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;
        WriteMcpConfig(job, providers.AgentProvider.McpConfigPath, _logger, providers.AgentProvider.ProviderType);

        var executor = new BrainConsolidationExecutor(_logger, _brainUpdateService, progress);
        return await executor.ExecuteAsync(job, providers.BrainProvider, providers.AgentProvider, ct, emitOutputLine);
    }

    private async Task<ConsolidationJobResult> ExecuteRefactoringDetectionAsync(
        ConsolidationJobMessage job, OrchestratorProxy orchestratorProxy,
        ConsolidationProgress? progress, Action<string> emitOutputLine, CancellationToken ct)
    {
        var resolution = await _resolver.ResolveRefactoringProvidersAsync(job, orchestratorProxy, ct);
        if (!resolution.IsSuccess)
            return resolution.Failure!;

        await using var providers = resolution.Providers!;
        WriteMcpConfig(job, providers.AgentProvider.McpConfigPath, _logger, providers.AgentProvider.ProviderType);

        var executor = new RefactoringExecutor(_logger, progress);
        return await executor.ExecuteAsync(job, providers.RepoProvider, providers.BrainProvider,
            providers.IssueProvider, providers.AgentProvider, ct, emitOutputLine);
    }
}
