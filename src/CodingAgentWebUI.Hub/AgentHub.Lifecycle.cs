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
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportJobCompleted(JobId jobId, JobCompletionPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        await _lifecycleService.HandleJobCompletedAsync(jobId, agent, payload, CancellationToken.None);
    }

    // ── Real-time status ────────────────────────────────────────────────

    /// <summary>
    /// Updates the PipelineRun's CurrentStep and HighWaterMark, applies optional step metadata, notifies UI.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null)
    {
        _lifecycleService.HandleStepTransition(jobId, step, timestamp, metadata);

        // Clear orphan-restored flag: agent is actively progressing on this job
        var agent = _facade.GetByConnectionId(Context.ConnectionId);
        if (agent is { OrphanRestoredAt: not null })
        {
            _logger.Information(
                "Agent {AgentId} reported progress on job {JobId}, clearing orphan-restored state",
                agent.AgentId, jobId.Value);
            agent.OrphanRestoredAt = null;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Reports the result of brain repository synchronization so the UI can display context status.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportBrainSyncResult(JobId jobId, bool contextLoaded, int knowledgeFileCount)
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

        return Task.CompletedTask;
    }

    /// <summary>
    /// Enqueues output lines into the run's OutputRingBuffer and the run's OutputLines queue.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportOutputLines(JobId jobId, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var buffer = _facade.GetOutputBuffer(jobId);
        buffer.AddRange(lines);

        // Also add to the run's OutputLines for UI streaming
        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            foreach (var line in lines)
                run.OutputLines.Enqueue(line);

            _changeNotifier.NotifyChange();
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Adds a chat entry to the run's chat history.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportChatEntry(JobId jobId, ChatRole role, string content)
    {
        ArgumentNullException.ThrowIfNull(content);

        var run = _facade.GetRun(jobId);
        run?.ChatHistory.Enqueue(new ChatEntry { Role = role, Content = content, Timestamp = DateTime.UtcNow });

        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the run's quality gate report and history.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportQualityGateResult(JobId jobId, QualityGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            _logger.Information("Job {JobId} quality gate result received", jobId.Value);
        }

        return Task.CompletedTask;
    }
}
