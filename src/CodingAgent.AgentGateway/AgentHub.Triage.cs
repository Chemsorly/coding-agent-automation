using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgent.AgentGateway;

public sealed partial class AgentHub
{
    // ── Triage ──

    /// <summary>
    /// Records the result of the caller's own triage run. <see cref="RequiresActiveJobAttribute"/> binds the
    /// job to the calling agent; <see cref="IHubTriageOperations.RecordResultAsync"/> checks that the job is a
    /// triage run and finds its triage from the WorkItem record.
    /// Called by the agent's <c>OrchestratorProxy.ReportTriageResultAsync</c>.
    /// </summary>
    [RequiresActiveJob]
    public Task ReportTriageResult(JobId jobId, string resultJson)
    {
        ArgumentException.ThrowIfNullOrEmpty(resultJson);

        if (_triageOps is null)
            throw new HubException("Triage results are not accepted by this host");

        // Not tied to the connection: a result the agent sent is recorded even if it disconnects meanwhile
        return _triageOps.RecordResultAsync(jobId, resultJson, CancellationToken.None);
    }

    /// <summary>
    /// Refuses issue creation for triage runs: a triage proposes drafts, and only a person creates issues from
    /// them, in the app. Fails closed when the run cannot be identified.
    /// </summary>
    private async Task RefuseIssueCreationForTriageAsync(JobId jobId)
    {
        if (await IsTriageRunAsync(jobId))
        {
            _logger.Warning("Issue creation refused for job {JobId}: triage runs may not create issues", jobId.Value);
            throw new HubException($"Job {jobId.Value} is a triage run; triage runs may not create issues");
        }
    }

    /// <summary>Whether the job is a triage run; fails closed (true) when the run cannot be identified.</summary>
    private async Task<bool> IsTriageRunAsync(JobId jobId) =>
        _triageOps is null
            // Without triage operations no triage runs exist on this host; only the in-memory run can tell.
            ? _facade.GetRun(jobId) is { RunType: PipelineRunType.Triage }
            : await _triageOps.IsTriageRunAsync(jobId, CancellationToken.None);
}
