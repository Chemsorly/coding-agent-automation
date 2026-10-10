using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>Triage operations of the agent hub.</summary>
public interface IHubTriageOperations
{
    /// <summary>
    /// Records the result an agent reports for its own triage run. The triage is found from the run's
    /// WorkItem record, never from what the agent sends.
    /// </summary>
    Task RecordResultAsync(JobId jobId, string resultJson, CancellationToken ct);

    /// <summary>
    /// True when the job is a triage run. Fails closed: a job whose run and WorkItem cannot be found counts as
    /// a triage run, so callers that refuse triage runs refuse it too.
    /// </summary>
    Task<bool> IsTriageRunAsync(JobId jobId, CancellationToken ct);
}

/// <inheritdoc cref="IHubTriageOperations"/>
public sealed class HubTriageOperations : IHubTriageOperations
{
    private readonly IAgentHubFacade _facade;
    private readonly ITriageStore _store;
    private readonly ILogger _logger;

    public HubTriageOperations(IAgentHubFacade facade, ITriageStore store, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(facade);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(logger);
        _facade = facade;
        _store = store;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RecordResultAsync(JobId jobId, string resultJson, CancellationToken ct)
    {
        var record = await _facade.GetWorkItemRunRecordAsync(jobId, ct);
        if (record is null || record.TaskType != WorkItemTaskType.Triage)
        {
            _logger.Warning("ReportTriageResult refused for job {JobId}: it is not a triage run", jobId.Value);
            throw new HubException($"Job {jobId.Value} is not a triage run");
        }

        if (record.ProjectId is not { } projectId)
            throw new HubException($"Triage job {jobId.Value} has no project");

        var parsed = TriageResultParser.Parse(resultJson);
        if (parsed.Result is null)
        {
            _logger.Warning("ReportTriageResult refused for job {JobId}: {Error}", jobId.Value, parsed.Error);
            throw new HubException($"The triage result of job {jobId.Value} could not be read: {parsed.Error}");
        }

        var run = _facade.GetRun(jobId);
        var report = new TriageResultReport
        {
            WorkItemId = jobId.Value,
            ProjectId = projectId.ToString("D"),
            Source = TriageConstants.IsOperatorTriage(record.IssueProviderConfigId) ? TriageSource.Operator : TriageSource.Issue,
            KeyProviderConfigId = record.IssueProviderConfigId,
            KeyIdentifier = record.IssueIdentifier,
            IssueTitle = run?.IssueTitle,
            IssueUrl = run?.IssueUrl,
            StartedAt = run?.StartedAtOffset ?? DateTimeOffset.UtcNow,
            Result = parsed.Result,
        };

        try
        {
            var saved = await _store.RecordResultAsync(report, ct);
            _logger.Information(
                "Recorded triage result for job {JobId} on triage {TriageId}: {Verdict}, {Checks} checks, {Drafts} drafts",
                jobId.Value, saved.Id, parsed.Result.Verdict, parsed.Result.Investigated.Count, parsed.Result.Drafts.Count);
        }
        catch (InvalidOperationException ex)
        {
            _logger.Error(ex, "ReportTriageResult failed for job {JobId}", jobId.Value);
            throw new HubException($"The triage of job {jobId.Value} could not be found");
        }
    }

    /// <inheritdoc/>
    public async Task<bool> IsTriageRunAsync(JobId jobId, CancellationToken ct)
    {
        if (_facade.GetRun(jobId) is { } run)
            return run.RunType == PipelineRunType.Triage;

        var record = await _facade.GetWorkItemRunRecordAsync(jobId, ct);
        return record is null || record.TaskType == WorkItemTaskType.Triage;
    }
}
