using System.Diagnostics;
using System.Text.Json;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// K8s-mode BackgroundService that orchestrates a single work item lifecycle:
/// HTTP GET assignment → exit 0 if terminal → POST Running → connect SignalR for logs/tokens
/// → execute pipeline → POST terminal status.
/// </summary>
/// <remarks>
/// Replaces <see cref="AgentWorkerService"/> in K8s mode. The agent is ephemeral —
/// one pod per work item, exits after completion.
/// </remarks>
public sealed class WorkItemAgentService : BackgroundService
{
    private readonly string _workItemId;
    private readonly WorkItemHttpClient _workItemClient;
    private readonly HubConnectionManager _hubManager;
    private readonly HubConnectionManagerFactory _hubManagerFactory;
    private readonly LocalPipelineExecutor _executor;
    private readonly LocalConsolidationExecutor _consolidationExecutor;
    private readonly AgentIdentity _agentIdentity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Serilog.ILogger _logger;

    private volatile CancellationTokenSource? _pipelineCts;
    private volatile bool _terminalStatusPosted;

    public WorkItemAgentService(
        string workItemId,
        WorkItemHttpClient workItemClient,
        HubConnectionManager hubManager,
        HubConnectionManagerFactory hubManagerFactory,
        LocalPipelineExecutor executor,
        LocalConsolidationExecutor consolidationExecutor,
        AgentIdentity agentIdentity,
        IHostApplicationLifetime lifetime,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(hubManager);
        ArgumentNullException.ThrowIfNull(hubManagerFactory);
        ArgumentNullException.ThrowIfNull(executor);
        ArgumentNullException.ThrowIfNull(consolidationExecutor);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _workItemId = workItemId;
        _workItemClient = workItemClient;
        _hubManager = hubManager;
        _hubManagerFactory = hubManagerFactory;
        _executor = executor;
        _consolidationExecutor = consolidationExecutor;
        _agentIdentity = agentIdentity;
        _lifetime = lifetime;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("WorkItemAgent.Execute");
        activity?.SetTag("work_item_id", _workItemId);
        activity?.SetTag("agent_id", _agentIdentity.Id);

        int exitCode = 1;
        try
        {
            exitCode = await RunWorkItemLifecycleAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.Information("WorkItemAgentService cancelled via SIGTERM for work item {WorkItemId}", _workItemId);
            await PostCancelledStatusAsync();
            exitCode = 0;
        }
        catch (WorkItemFetchException ex)
        {
            _logger.Error(ex, "Failed to fetch assignment for work item {WorkItemId}", _workItemId);
            exitCode = 1;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "WorkItemAgentService failed for work item {WorkItemId}", _workItemId);
            exitCode = 1;
        }
        finally
        {
            activity?.SetTag("exit_code", exitCode);
            if (exitCode != 0)
                activity?.SetStatus(ActivityStatusCode.Error);

            // Set the process exit code and stop the host
            Environment.ExitCode = exitCode;
            _lifetime.StopApplication();
        }
    }

    private async Task<int> RunWorkItemLifecycleAsync(CancellationToken ct)
    {
        // Step 1: Fetch assignment
        _logger.Information("Fetching assignment for work item {WorkItemId}", _workItemId);
        var assignment = await _workItemClient.GetAssignmentAsync(_workItemId, ct);

        if (assignment is null)
        {
            // Terminal status — previous attempt already reported. Exit 0.
            _logger.Information("Work item {WorkItemId} already terminal, exiting cleanly", _workItemId);
            return 0;
        }

        _logger.Information("Received assignment for work item {WorkItemId}: issue={IssueIdentifier}, runType={RunType}",
            _workItemId, assignment.IssueIdentifier, assignment.RunType);

        // Step 2: POST Running
        var runningUpdate = new WorkItemStatusUpdate
        {
            Status = "Running",
            AgentId = _agentIdentity.Id
        };
        var accepted = await _workItemClient.PostStatusAsync(_workItemId, runningUpdate, ct);
        if (!accepted)
        {
            _logger.Warning("Status transition to Running was rejected for work item {WorkItemId}", _workItemId);
            // Already transitioned (idempotent) or invalid state — proceed anyway
        }

        // Step 3: Connect SignalR for logs/tokens
        try
        {
            await _hubManager.StartAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Error(ex, "Failed to connect SignalR hub for work item {WorkItemId}, posting Failed", _workItemId);
            await PostFailedStatusAsync($"SignalR connection failed: {ex.Message}");
            return 1;
        }
        _logger.Information("Connected to SignalR hub for log streaming/token vending");

        // Step 4: Execute pipeline
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pipelineCts = pipelineCts;
        var pipelineCt = pipelineCts.Token;

        await using var outputBatcher = new OutputBatcher();
        outputBatcher.OnFlush += async lines =>
        {
            try
            {
                await _hubManager.Connection.InvokeAsync(
                    HubMethodNames.ReportOutputLines, assignment.JobId, lines);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to send output lines batch via SignalR");
            }
        };

        JobCompletionPayload? completion = null;
        try
        {
            completion = await _executor.ExecuteAsync(
                assignment, _hubManager.Connection, outputBatcher,
                step => { /* K8s mode doesn't need step tracking for heartbeats */ },
                pipelineCt);
        }
        catch (OperationCanceledException) when (pipelineCt.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Pipeline cancelled externally (not SIGTERM)
            completion = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = assignment.LinkedPullRequest is not null
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // SIGTERM — propagate up for SIGTERM handler
            throw;
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline execution failed for work item {WorkItemId}", _workItemId);
            completion = new JobCompletionPayload
            {
                FinalStep = PipelineStep.Failed,
                FailureReason = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = assignment.LinkedPullRequest is not null
            };
        }

        // Step 5: POST terminal status
        var terminalStatus = completion.FinalStep switch
        {
            PipelineStep.Completed => "Succeeded",
            PipelineStep.Cancelled => "Cancelled",
            _ => "Failed"
        };

        var terminalUpdate = new WorkItemStatusUpdate
        {
            Status = terminalStatus,
            AgentId = _agentIdentity.Id,
            Result = SerializeResult(completion),
            ErrorMessage = completion.FailureReason
        };

        try
        {
            _terminalStatusPosted = true;
            await _workItemClient.PostStatusAsync(_workItemId, terminalUpdate, CancellationToken.None);
        }
        catch (WorkItemStatusPostException ex)
        {
            // All retries exhausted for terminal status POST.
            // ReconciliationService will catch via Job status. Log and exit non-zero.
            _logger.Error(ex, "Failed to POST terminal status for work item {WorkItemId}, exiting non-zero", _workItemId);
            return 1;
        }

        // Also report completion via SignalR (for real-time UI updates)
        try
        {
            await _hubManager.Connection.InvokeAsync(
                HubMethodNames.ReportJobCompleted, assignment.JobId, completion);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report completion via SignalR (non-fatal, HTTP status already posted)");
        }
        finally
        {
            try { await _hubManager.StopAsync(CancellationToken.None); }
            catch { /* best-effort cleanup */ }
        }

        return 0;
    }

    /// <summary>
    /// Cancels the running pipeline. Called from the SIGTERM handler.
    /// </summary>
    internal void CancelPipeline()
    {
        try { _pipelineCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task PostCancelledStatusAsync()
    {
        if (_terminalStatusPosted) return; // Terminal already posted, skip

        try
        {
            var cancelUpdate = new WorkItemStatusUpdate
            {
                Status = "Cancelled",
                AgentId = _agentIdentity.Id,
                ErrorMessage = "Agent received SIGTERM"
            };
            await _workItemClient.PostStatusAsync(_workItemId, cancelUpdate, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to POST Cancelled status after SIGTERM (ReconciliationService will handle)");
        }
    }

    private async Task PostFailedStatusAsync(string errorMessage)
    {
        if (_terminalStatusPosted) return;

        try
        {
            var failUpdate = new WorkItemStatusUpdate
            {
                Status = "Failed",
                AgentId = _agentIdentity.Id,
                ErrorMessage = errorMessage,
                FailureReason = "AgentError"
            };
            await _workItemClient.PostStatusAsync(_workItemId, failUpdate, CancellationToken.None);
            _terminalStatusPosted = true;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to POST Failed status (ReconciliationService will handle)");
        }
    }

    private string? SerializeResult(JobCompletionPayload? completion)
    {
        if (completion is null) return null;
        try
        {
            return JsonSerializer.Serialize(completion, PipelineJsonOptions.Default);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to serialize JobCompletionPayload — result field will be omitted from terminal status");
            return null;
        }
    }
}
