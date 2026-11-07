using System.Diagnostics;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Spec 056 DD11 (req 6.2–6.7): an agent reported that its credential cannot be used. Exactly once per run, whichever
/// completion path arrives first:
/// <list type="number">
///   <item>end the item: Dispatched or Running → Failed (<c>ProviderUnavailable</c>), only for the agent that runs it;
///     a loser (another path, a late report, another agent) stops here;</item>
///   <item>pause the item's credential (source <c>auto</c>, until the reset time when known);</item>
///   <item>end the run: history row, queue label back, agent state cleared, Job deleted;</item>
///   <item>queue the same work as a new Pending item with the retry count, priority and enqueue time, held for the
///     paused credential;</item>
///   <item>record <c>pipeline.run.outcomes</c> (<c>outcome=requeued</c>), the span attribute and one log line.</item>
/// </list>
/// A requeue never moves an item back to Pending.
/// </summary>
internal sealed class ProviderFailureHandler(
    IDbContextFactory<PipelineDbContext> dbFactory,
    WorkItemTransitionService transitions,
    CredentialPauseStore pauses,
    IRunLifecycleManager lifecycle,
    WorkItemStore store,
    TimeProvider time,
    Serilog.ILogger logger) : IProviderFailureHandler
{
    private readonly Serilog.ILogger _log = logger.ForContext<ProviderFailureHandler>();

    public async Task<bool> HandleAsync(Guid workItemId, string reporter, AgentProviderFailure failure, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(reporter);
        ArgumentNullException.ThrowIfNull(failure);
        Activity.Current?.SetTag(PipelineTelemetry.CredentialAttributes.ProviderFailure, failure.Category);

        // The row as dispatched: its credential, request, Job and carried fields.
        Infrastructure.Persistence.Entities.WorkItemEntity? before;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
            before = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId, ct);
        if (before is null)
            return false;

        var credentialName = before.Credential is { } c ? AgentCredentials.DisplayName(c) : "the agent credential";
        var detail = CredentialPauseStore.CleanDetail(failure.Detail);
        var text = HoldTexts.Cut($"Requeued: {credentialName} paused — {detail}");
        var now = time.GetUtcNow();

        // 1. Compare-and-swap: only the agent that runs the item, only while it runs, only once.
        var won = await transitions.TransitionIfAsync(
            workItemId,
            w => w.Status is WorkItemStatus.Dispatched or WorkItemStatus.Running
                && (string.Equals(w.K8sJobName, reporter, StringComparison.Ordinal)
                    || string.Equals(w.AssignedAgentId, reporter, StringComparison.Ordinal)),
            WorkItemStatus.Failed,
            w =>
            {
                w.FailureReason = FailureReason.ProviderUnavailable;
                w.ErrorMessage = text;
                w.CompletedAt = now;
            },
            ct);
        if (!won)
        {
            _log.Debug("Provider failure for work item {WorkItemId} from {Reporter} already handled or not its own", workItemId,
                LogSanitizer.SanitizeForLog(reporter));
            return false;
        }

        // Once the item is Failed every other path loses the swap, so nothing below may abort the requeue: the new item
        // is created before the run is ended (its label swap would let the issue loop queue the issue first), its
        // creation is retried, and the pause and the run's end only log their failures.

        // 2. Pause the credential of the route the item ran on.
        AgentCredentialPause? intended = null;
        if (before.Credential is { } credential)
        {
            intended = new AgentCredentialPause(
                credential,
                failure.Category == AgentProviderFailure.AuthFailed ? CredentialPauseReasons.AuthFailed : CredentialPauseReasons.QuotaExhausted,
                CredentialPauseSources.Auto,
                now,
                failure.ResetsAt,
                detail,
                WorkItemId: workItemId,
                RunId: workItemId.ToString());
            try
            {
                await pauses.PauseAsync(intended, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "Pausing credential {Credential} for work item {WorkItemId} failed; the work is still queued again",
                    credential, workItemId);
            }
        }
        else
        {
            _log.Warning("Work item {WorkItemId} carries no credential; the provider failure pauses nothing", workItemId);
        }

        // 3. The same work, queued again; held until the credential is available.
        WorkItemPayload.TryDeserialize(before.Payload, out var request);
        var newId = Guid.Empty;
        if (request is not null)
            newId = await QueueAgainAsync(before, request, intended, now, ct);
        else
            _log.Error("Work item {WorkItemId}'s request cannot be read; it was ended but not queued again", workItemId);

        // 4. End the run: history row, queue label back, agent state, Job.
        try
        {
            await lifecycle.RequeueRunAsync(
                new RunId(workItemId.ToString()), text,
                new RequeueTarget(
                    request?.RunType ?? before.TaskType.ToDefaultRunTypeOrNull() ?? PipelineRunType.Implementation,
                    before.IssueIdentifier, before.IssueProviderConfigId, request?.RepoProviderConfigId,
                    before.AssignedAgentId ?? before.K8sJobName),
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.Error(ex, "Ending the run of work item {WorkItemId} after its requeue failed", workItemId);
        }

        // 5. The outcome of the ended attempt.
        var (duration, runTypeTag, _, projectName, providerTag, modelTag) =
            await RunOutcomeTelemetry.ResolveRunContextAsync(workItemId, dbFactory, ct);
        RunOutcomeTelemetry.RecordRunOutcomeMetrics(
            runTypeTag, "requeued", "provider_unavailable", projectName, duration, providerTag, modelTag);

        // 6. One line per requeue (req 8.4).
        _log.Information(
            "Requeued work item {WorkItemId} ({IssueIdentifier}) as {NewWorkItemId}: {Credential} {ProviderFailure} — {PauseDetail}",
            workItemId, LogSanitizer.SanitizeForLog(before.IssueIdentifier), newId, before.Credential, failure.Category, detail);
        return true;
    }

    /// <summary>
    /// Creates the new Pending item with the ended item's retry count, priority and enqueue time, held for the paused
    /// credential. Retried on a transient failure; an item that already exists for the issue is logged, not an error.
    /// </summary>
    private async Task<Guid> QueueAgainAsync(
        Infrastructure.Persistence.Entities.WorkItemEntity before, JobDistributionRequest request,
        AgentCredentialPause? intended, DateTimeOffset now, CancellationToken ct)
    {
        const int attempts = 3;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                var pause = before.Credential is { } paused ? await pauses.LoadAsync(paused, ct) ?? intended : null;
                var (outcome, created) = await store.CreateAsync(
                    request with { RunId = Guid.NewGuid().ToString() },
                    ct,
                    new WorkItemCreateOptions(
                        before.RetryCount,
                        before.PriorityWeight,
                        before.OriginalEnqueuedAt ?? before.CreatedAt,
                        pause is null ? null : HoldReasons.CredentialPaused,
                        pause is null ? null : HoldTexts.CredentialPaused(pause),
                        before.Credential,
                        now));
                if (outcome == WorkItemCreateOutcome.Created)
                    return created;
                _log.Warning("Work item {WorkItemId} was not queued again: {CreateOutcome} (another work item for the issue exists)",
                    before.Id, outcome);
                return Guid.Empty;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < attempts)
            {
                _log.Warning(ex, "Queueing work item {WorkItemId} again failed (attempt {Attempt} of {Attempts}); retrying",
                    before.Id, attempt, attempts);
                await Task.Delay(TimeSpan.FromMilliseconds(200 * attempt), time, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.Error(ex, "Work item {WorkItemId} was ended but could not be queued again", before.Id);
                return Guid.Empty;
            }
        }
    }
}
