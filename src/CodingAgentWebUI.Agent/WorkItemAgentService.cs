using System.Diagnostics;
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
/// Uses <see cref="AgentConnectionManager"/> for shared connection lifecycle (heartbeat,
/// resilience, reconnection, CancelJob handling, deregistration).
/// Uses <see cref="IWorkItemExecutor"/> for unified task execution (routes by TaskType internally).
/// </remarks>
public sealed class WorkItemAgentService : BackgroundService, IAgentService
{
    private readonly string _workItemId;
    private readonly IWorkItemLifecycleClient _workItemClient;
    private readonly IAgentConnectionManager _connectionManager;
    private readonly IWorkItemExecutor _workItemExecutor;
    private readonly IJobCompletionReporter _completionReporter;
    private readonly AgentIdentity _agentIdentity;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly Serilog.ILogger _logger;

    private volatile CancellationTokenSource? _pipelineCts;
    private volatile bool _terminalStatusPosted;

    public WorkItemAgentService(
        string workItemId,
        IWorkItemLifecycleClient workItemClient,
        IAgentConnectionManager connectionManager,
        IWorkItemExecutor workItemExecutor,
        IJobCompletionReporter completionReporter,
        AgentIdentity agentIdentity,
        IHostApplicationLifetime lifetime,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(workItemId);
        ArgumentNullException.ThrowIfNull(workItemClient);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(workItemExecutor);
        ArgumentNullException.ThrowIfNull(completionReporter);
        ArgumentNullException.ThrowIfNull(agentIdentity);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);

        _workItemId = workItemId;
        _workItemClient = workItemClient;
        _workItemExecutor = workItemExecutor;
        _completionReporter = completionReporter;
        _agentIdentity = agentIdentity;
        _lifetime = lifetime;
        _logger = logger;

        // Use the injected connection manager
        _connectionManager = connectionManager;

        // Wire CancelJob to cancel the pipeline
        _connectionManager.OnCancelJobReceived += HandleCancelJobAsync;
        _connectionManager.OnForceDisconnect += HandleForceDisconnectAsync;
    }

    /// <inheritdoc/>
    public bool IsBusy => _pipelineCts is not null && !_pipelineCts.IsCancellationRequested;

    /// <inheritdoc/>
    public PipelineStep? CurrentStep => null; // K8s mode doesn't track steps at the service level (delegated to IWorkItemExecutor)

    /// <inheritdoc/>
    public bool IsConnected => _connectionManager.IsConnected;

    /// <inheritdoc/>
    public void CancelCurrentJob() => CancelPipeline();

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
            // Graceful deregistration + connection cleanup
            await _connectionManager.DisposeAsync();

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
            _logger.Warning("Status transition to Running was rejected for work item {WorkItemId} — aborting (work item already terminal or invalid state)", _workItemId);
            return 1;
        }

        // Step 3: Connect, register, and start heartbeat via AgentConnectionManager
        var labelsEnv = Environment.GetEnvironmentVariable(AgentDefaults.EnvAgentLabels) ?? string.Empty;
        var labels = labelsEnv
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();

        var registration = new AgentRegistrationMessage
        {
            AgentId = _agentIdentity.Id,
            Hostname = Environment.MachineName,
            Labels = labels,
            ActiveJob = new ActiveJobState
            {
                RunId = _workItemId,
                IssueIdentifier = assignment.IssueIdentifier,
                IssueTitle = assignment.IssueDetail?.Title ?? assignment.IssueIdentifier,
                IssueProviderConfigId = assignment.IssueProviderConfigId ?? assignment.RepoProviderConfigId,
                RepoProviderConfigId = assignment.RepoProviderConfigId,
                AgentProviderConfigId = assignment.AgentProviderConfigId,
                BrainProviderConfigId = assignment.BrainProviderConfigId,
                PipelineProviderConfigId = assignment.PipelineProviderConfigId,
                InitiatedBy = assignment.InitiatedBy,
                ResolvedProfileId = assignment.ResolvedProfileId,
                ProjectId = assignment.ProjectId,
                ProjectName = assignment.ProjectName,
                CurrentStep = PipelineStep.Created,
                StartedAt = DateTimeOffset.UtcNow,
                RunType = assignment.RunType
            }
        };

        try
        {
            await _connectionManager.ConnectAndRegisterAsync(registration, ct);
            _logger.Information("Registered agent {AgentId} with orchestrator hub (ActiveJob={WorkItemId})",
                _agentIdentity.Id, _workItemId);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Error(ex, "Failed to connect/register for work item {WorkItemId}, posting Failed", _workItemId);
            await PostFailedStatusAsync($"Connection/registration failed: {ex.Message}");
            return 1;
        }

        // Step 4: Execute work item via unified executor
        using var pipelineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pipelineCts = pipelineCts;
        var pipelineCt = pipelineCts.Token;

        await using var outputBatcher = new OutputBatcher();
        outputBatcher.OnFlush += async lines =>
        {
            try
            {
                await _connectionManager.InvokeAsync(
                    (conn, token) => conn.InvokeAsync(HubMethodNames.ReportOutputLines, assignment.JobId, lines, token), ct);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to send output lines batch via SignalR");
            }
        };

        JobCompletionPayload? completion = null;
        try
        {
            completion = await _workItemExecutor.ExecuteAsync(
                assignment, _connectionManager.Connection, outputBatcher,
                step => _connectionManager.UpdateCurrentStep(step),
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

        // Step 5: Report completion via unified reporter
        try
        {
            _terminalStatusPosted = true;
            await _completionReporter.ReportCompletionAsync(assignment.JobId, completion, CancellationToken.None);
        }
        catch (WorkItemStatusPostException ex)
        {
            _logger.Error(ex, "Failed to report completion for work item {WorkItemId}, exiting non-zero", _workItemId);
            return 1;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Non-fatal error during completion reporting for work item {WorkItemId}", _workItemId);
        }

        // Exit non-zero when pipeline did not complete successfully.
        return completion.FinalStep == PipelineStep.Completed ? 0 : 1;
    }

    /// <summary>
    /// Cancels the running pipeline. Called from the SIGTERM handler.
    /// </summary>
    internal void CancelPipeline()
    {
        try { _pipelineCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private Task HandleCancelJobAsync(string jobId)
    {
        _logger.Information("Received CancelJob for {JobId}, cancelling pipeline", jobId);
        CancelPipeline();
        return Task.CompletedTask;
    }

    private Task HandleForceDisconnectAsync()
    {
        _logger.Warning("Received ForceDisconnect, cancelling pipeline for graceful shutdown");
        CancelPipeline();
        return Task.CompletedTask;
    }

    private async Task PostCancelledStatusAsync()
    {
        if (_terminalStatusPosted) return;

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

}
