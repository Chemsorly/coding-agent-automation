using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
namespace CodingAgentWebUI.Hub;

public sealed partial class AgentHub
{
    // ── Job lifecycle ───────────────────────────────────────────────────

    /// <summary>
    /// Agent acknowledges job acceptance. Transitions agent to Busy and WorkItem to Running.
    /// </summary>
    [RequiresActiveJob]
    public async Task JobAccepted(JobId jobId)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobAcceptedAsync(jobId, agent, CancellationToken.None);
    }

    /// <summary>
    /// Agent rejects a job. Cleans up the orphaned run and reverts the label so the
    /// pipeline loop can re-discover and re-dispatch the issue.
    /// This should be rare after the atomic agent reservation fix in SelectAgent.
    /// </summary>
    [RequiresActiveJob]
    public async Task JobRejected(JobId jobId, string reason)
    {
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobRejectedAsync(jobId, agent, reason, CancellationToken.None);
    }

    /// <summary>
    /// Agent reports job completion. Updates the PipelineRun, persists to history,
    /// transitions agent to Idle, and signals the drain service for next dispatch.
    /// Also pushes <see cref="IAgentHubUiClient.OnRunCompleted"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportJobCompleted(JobId jobId, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);

        // Push completion event to subscribed UI circuits (Req 4.1, 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnRunCompleted, jobId.Value, payload);
    }

    // ── Real-time status ────────────────────────────────────────────────

    /// <summary>
    /// Updates the PipelineRun's CurrentStep and HighWaterMark, applies optional step metadata, notifies UI.
    /// Also pushes <see cref="IAgentHubUiClient.OnStepTransition"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null)
    {
        _lifecycleService.HandleStepTransition(jobId, step, timestamp, metadata);

        // Clear orphan-restored flag: agent is actively progressing on this job
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is { OrphanRestoredAt: not null })
        {
            _logger.Information(
                "Agent {AgentId} reported progress on job {JobId}, clearing orphan-restored state",
                agent.AgentId, jobId.Value);
            // Clear on the local object immediately (for in-memory tests and single-replica deployments)
            agent.OrphanRestoredAt = null;
            // Also propagate to distributed registry so the write is visible to other replicas
            _ = _facade.UpdateAgentFieldAsync(agent.AgentId, "orphanRestoredAt", null);
        }

        // Push step transition event to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnStepTransition, jobId.Value, step, timestamp);
    }

    /// <summary>
    /// Reports the result of brain repository synchronization so the UI can display context status.
    /// Also pushes <see cref="IAgentHubUiClient.OnBrainSyncResult"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportBrainSyncResult(JobId jobId, bool contextLoaded, int knowledgeFileCount)
    {
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.BrainContextLoaded = contextLoaded;
            run.BrainKnowledgeFileCount = knowledgeFileCount;
            _logger.Debug("Job {JobId} brain sync result: loaded={Loaded}, files={FileCount}",
                jobId.Value, contextLoaded, knowledgeFileCount);
            _changeNotifier.NotifyChange();
        }

        // Push brain sync result to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnBrainSyncResult, jobId.Value, contextLoaded, knowledgeFileCount);
    }

    /// <summary>
    /// Enqueues output lines into the run's OutputRingBuffer and the run's OutputLines queue.
    /// Also pushes <see cref="IAgentHubUiClient.OnOutputLines"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportOutputLines(JobId jobId, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        // Write to ring buffer (in-memory) and/or Redis List (distributed).
        // GetOutputBuffer ensures the buffer exists; AddRange writes the lines.
        // AppendOutputLines handles distributed (Redis) persistence when configured.
        var buffer = _facade.GetOutputBuffer(jobId);
        buffer.AddRange(lines);
        _facade.AppendOutputLines(jobId, lines);

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            // Also enqueue into run.OutputLines for in-memory consumers (UI components, tests).
            // Under distributed mode GetRun() returns a local snapshot; this write is best-effort
            // and the authoritative backlog comes from GetOutputBacklogAsync (Redis LRANGE).
            foreach (var line in lines)
                run.OutputLines.Enqueue(line);

            _changeNotifier.NotifyChange();
        }

        // Push output lines to subscribed UI circuits (Req 2.4, 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnOutputLines, jobId.Value, lines);
    }

    /// <summary>
    /// Adds a chat entry to the run's chat history.
    /// Also pushes <see cref="IAgentHubUiClient.OnChatEntry"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportChatEntry(JobId jobId, ChatRole role, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var run = _facade.GetRun(jobId);
        run?.ChatHistory.Enqueue(new ChatEntry { Role = role, Content = content, Timestamp = DateTime.UtcNow });

        // Push chat entry to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnChatEntry, jobId.Value, role, content);
    }

    /// <summary>
    /// Updates the run's quality gate report and history.
    /// Also pushes <see cref="IAgentHubUiClient.OnQualityGateResult"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportQualityGateResult(JobId jobId, QualityGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            _logger.Information("Job {JobId} quality gate result received", jobId.Value);
        }

        // Push quality gate result to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnQualityGateResult, jobId.Value, report);
    }
}
