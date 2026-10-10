using CodingAgent.Contracts;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.SignalR;
namespace CodingAgent.AgentGateway;

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
    /// This should be rare; the dispatch path prevents double-booking via the DB unique
    /// constraint and the IsIssueBeingProcessed guard.
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
        // TODO: Pass Context.ConnectionAborted instead of CancellationToken.None so that
        // PostCompletionBookkeepingAsync is also cancellable on connection abort (not only on
        // host shutdown via IHostApplicationLifetime.ApplicationStopping). Currently, the linked
        // CancellationTokenSource inside PostCompletionBookkeepingAsync only uses ApplicationStopping
        // as its effective cancellation source — the connection-abort path is unguarded.
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
            _facade.UpdateAgentFieldFireAndForget(agent.AgentId, AgentFieldNames.OrphanRestoredAt, null, _logger, "ReportStepTransition");
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
            _facade.ReplaceRun(run);
            _logger.Debug("Job {JobId} brain sync result: loaded={Loaded}, files={FileCount}",
                jobId.Value, contextLoaded, knowledgeFileCount);
            _changeNotifier.NotifyChange();
        }
        else
        {
            _logger.Warning("ReportBrainSyncResult: job {JobId} run not found — brain sync result discarded (run may have completed or expired)",
                jobId.Value);
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
        // AppendOutputLines handles all persistence paths.
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

        // Stored through the run service: in Redis mode GetRun returns a copy, so enqueuing on it would be lost.
        _facade.AppendChatEntry(jobId, new ChatEntry { Role = role, Content = content, Timestamp = DateTime.UtcNow });

        // Push chat entry to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnChatEntry, jobId.Value, role, content);
    }

    /// <summary>
    /// Updates the run's quality gate report and history. Also records gate result metrics
    /// server-side (run_type × gate × result × infrastructure_failure).
    /// Also pushes <see cref="IAgentHubUiClient.OnQualityGateResult"/> to the run group.
    /// </summary>
    [RequiresActiveJob]
    public async Task ReportQualityGateResult(JobId jobId, QualityGateReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId);
        if (run is not null)
        {
            // Stored through the run service: in Redis mode GetRun returns a copy,
            // so enqueuing on it directly would be lost. Use AppendQualityGateReport
            // which writes to the Redis list (distributed) or the live run object (in-memory).
            _facade.AppendQualityGateReport(jobId, report);
            run.LatestQualityReport = report;
            _facade.ReplaceRun(run);
            _logger.Information("Job {JobId} quality gate result received", jobId.Value);

            // Record gate result metrics server-side (issue #2979).
            // Agent pods must NOT record these — the API is the authoritative recording site.
            RecordQualityGateResultMetrics(run.RunType, report);
        }

        // Push quality gate result to subscribed UI circuits (Req 5.2)
        await _uiContext.Clients.Group($"run-{jobId.Value}")
            .SendAsync(HubMethodNames.OnQualityGateResult, jobId.Value, report);
    }

    /// <summary>
    /// Records <c>pipeline.run.quality_gate.results</c> counter for each gate in the report.
    /// Called by <see cref="ReportQualityGateResult"/> on the API side.
    /// Uses the QgcResults list when present (multi-QGC mode) so each QGC pair contributes
    /// its own compilation and tests gate measurement; otherwise falls back to the aggregate
    /// flat fields for backward compat with single-QGC payloads.
    /// </summary>
    private static void RecordQualityGateResultMetrics(PipelineRunType runType, QualityGateReport report)
    {
        // In multi-QGC mode, the aggregate flat fields collapse per-QGC detail so we record
        // from QgcResults instead. In single-QGC mode QgcResults is empty and we use flat fields.
        if (report.QgcResults.Count > 0)
        {
            foreach (var qgcResult in report.QgcResults)
            {
                if (qgcResult.Compilation is not null)
                    RecordGate(runType, PipelineTelemetry.QualityGateResultGates.Compilation,
                        qgcResult.Compilation.Passed, infraFailure: false);

                if (qgcResult.Tests is not null)
                    RecordGate(runType, PipelineTelemetry.QualityGateResultGates.Tests,
                        qgcResult.Tests.Passed, infraFailure: qgcResult.Tests.IsInfrastructureFailure == true);
            }
        }
        else
        {
            RecordGate(runType, PipelineTelemetry.QualityGateResultGates.Compilation,
                report.Compilation.Passed, infraFailure: false);
            RecordGate(runType, PipelineTelemetry.QualityGateResultGates.Tests,
                report.Tests.Passed, infraFailure: report.Tests.IsInfrastructureFailure == true);
        }

        // ExternalCi is always from the flat field (single entry per report).
        if (report.ExternalCi is not null)
            RecordGate(runType, PipelineTelemetry.QualityGateResultGates.ExternalCi,
                report.ExternalCi.Passed, infraFailure: false);
    }

    private static void RecordGate(PipelineRunType runType, string gate, bool passed, bool infraFailure)
    {
        PipelineTelemetry.RunQualityGateResults.Add(1,
            PipelineTelemetry.RunTypeTag(runType),
            new KeyValuePair<string, object?>("gate", gate),
            new KeyValuePair<string, object?>("result", passed ? "pass" : "fail"),
            new KeyValuePair<string, object?>("infrastructure_failure", infraFailure ? "true" : "false"));
    }

    /// <summary>
    /// Records a discrete pipeline run event (CI re-trigger, CI wait, agent stall) as a server-side metric.
    /// Agent pods call this hub method instead of recording metrics locally, avoiding the first-increment
    /// Prometheus gap on counters that fire once per pod lifetime.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportPipelineRunEvent(JobId jobId, PipelineRunEventReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var run = _facade.GetRun(jobId);
        // TODO [WARNING]: When _facade.GetRun returns null (race between job completion and event arrival),
        // runType defaults to PipelineRunType.Implementation, silently mis-attributing metrics for any
        // non-implementation run type. [RequiresActiveJob] makes this unlikely but not impossible.
        // Consider logging a Warning when run is null (consistent with how other hub methods handle this),
        // or skipping the metric recording entirely when the run cannot be resolved. (DotNetSpecialist #2979)
        var runType = run?.RunType ?? PipelineRunType.Implementation;

        switch (report.Kind)
        {
            case PipelineRunEventKind.CiNotStartedRetrigger:
                PipelineTelemetry.RunCiNotStartedRetriggers.Add(1,
                    PipelineTelemetry.RunTypeTag(runType));
                _logger.Debug("Job {JobId} CI not-started retrigger recorded", jobId.Value);
                break;

            case PipelineRunEventKind.CiWait:
                if (report.DurationSeconds.HasValue && report.Stage is not null && report.Result is not null)
                {
                    // TODO [WARNING]: report.Stage and report.Result are forwarded directly from the agent
                    // payload without normalization against the closed sets (PipelineTelemetry.CiWaitStages,
                    // PipelineTelemetry.AgentStallKinds). A compromised or buggy agent pod could send arbitrary
                    // strings, inflating label cardinality on the metrics backend. Consider validating against
                    // the closed sets and logging+skipping unknown values. (SecurityReviewer #2979)
                    PipelineTelemetry.RunCiWait.Record(report.DurationSeconds.Value,
                        PipelineTelemetry.RunTypeTag(runType),
                        new KeyValuePair<string, object?>("stage", report.Stage),
                        new KeyValuePair<string, object?>("result", report.Result));
                    _logger.Debug("Job {JobId} CI wait recorded: stage={Stage} result={Result} duration={Duration:F1}s",
                        jobId.Value, report.Stage, report.Result, report.DurationSeconds.Value);
                }
                else
                {
                    _logger.Warning("Job {JobId} CiWait event missing required fields (DurationSeconds, Stage, Result) — skipped",
                        jobId.Value);
                }
                break;

            case PipelineRunEventKind.AgentStall:
                if (report.Stage is not null
                    && PipelineTelemetry.AgentStallKinds.Normalize(report.Result) is { } stallKind)
                {
                    // Both tags are mapped onto closed sets so an agent (including an older image that
                    // still reports qgc_retry_agent / code_review / unknown) cannot add label values.
                    var phase = PipelineTelemetry.NormalizeRunPhase(report.Stage);
                    PipelineTelemetry.RunAgentStalls.Add(1,
                        PipelineTelemetry.RunTypeTag(runType),
                        new KeyValuePair<string, object?>("phase", phase),
                        new KeyValuePair<string, object?>("kind", stallKind));
                    _logger.Debug("Job {JobId} agent stall recorded: phase={Phase} kind={Kind}",
                        jobId.Value, phase, stallKind);
                }
                else
                {
                    _logger.Warning("Job {JobId} AgentStall event without a stage or with unknown kind {Kind} — skipped",
                        jobId.Value, report.Result);
                }
                break;

            default:
                _logger.Warning("Job {JobId} unknown PipelineRunEventKind {Kind} — ignored",
                    jobId.Value, report.Kind);
                break;
        }

        return Task.CompletedTask;
    }
}
