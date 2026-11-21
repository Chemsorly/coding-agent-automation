using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// The usage threshold of the Claude subscription: stores each agent call's readings and, when a window reaches its
/// threshold (<see cref="CredentialUsageThresholds"/>), pauses the credential until that window resets (reason
/// <c>usage_threshold</c>, source <c>auto</c>). The pause holds new work only; runs already going finish. A manual pause
/// wins and the later end wins (<see cref="CredentialPauseRules"/>). Pause decisions, operator resumes and threshold
/// changes run under <see cref="CredentialUsageStore.DecisionLockName"/>, so a resume always sticks.
/// </summary>
internal sealed class CredentialUsageMonitor(
    IDbContextFactory<PipelineDbContext> dbFactory,
    CredentialUsageStore usage,
    CredentialPauseStore pauses,
    TimeProvider time,
    Serilog.ILogger logger) : ICredentialUsageObserver
{
    private readonly Serilog.ILogger _log = logger.ForContext<CredentialUsageMonitor>();

    public async Task ObserveAsync(Guid workItemId, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readings);
        if (readings.Count == 0)
            return;

        string? credential;
        await using (var db = await dbFactory.CreateDbContextAsync(ct))
        {
            credential = await db.WorkItems.AsNoTracking()
                .Where(w => w.Id == workItemId).Select(w => w.Credential).FirstOrDefaultAsync(ct);
        }

        await ObserveAsync(credential, readings, workItemId, ct);
    }

    public Task ObserveCredentialAsync(string credential, IReadOnlyList<AgentRateLimitObservation> readings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(readings);
        return readings.Count == 0 ? Task.CompletedTask : ObserveAsync(credential, readings, workItemId: null, ct);
    }

    /// <summary>
    /// An operator resumed <paramref name="credential"/>: for the Claude subscription its windows over their threshold
    /// are let through until they reset; then the pause ends. Both under the decision lock.
    /// </summary>
    public async Task ResumeAsync(string credential, string by, CancellationToken ct)
    {
        if (credential != AgentCredentials.ClaudeSubscription)
        {
            await pauses.ResumeAsync(credential, by, ct);
            return;
        }

        await using var decision = await usage.LockDecisionAsync(ct);
        await usage.ResumeAsync(credential, ct);
        await pauses.ResumeAsync(credential, by, ct);
    }

    /// <summary>
    /// The thresholds changed: checks the last readings against them at once. A window now over its threshold pauses
    /// the subscription; a <c>usage_threshold</c> pause that no window justifies any more ends (an operator's pause and
    /// a used-up quota stay).
    /// </summary>
    public async Task ReevaluateAsync(string by, CancellationToken ct)
    {
        const string credential = AgentCredentials.ClaudeSubscription;
        await using var decision = await usage.LockDecisionAsync(ct);
        var breach = await BreachAsync(credential, ct);
        if (breach is not null)
        {
            await PauseAsync(credential, breach, workItemId: null, $"a threshold change by {LogSanitizer.SanitizeForLog(by)}", ct);
            return;
        }

        if (await pauses.LoadAsync(credential, ct) is { Source: CredentialPauseSources.Auto, Reason: CredentialPauseReasons.UsageThreshold })
        {
            await pauses.ResumeAsync(credential, by, ct);
            _log.Information("Usage threshold pause of {Credential} ended: no window is over its threshold after the change by {User}",
                credential, LogSanitizer.SanitizeForLog(by));
        }
    }

    private async Task ObserveAsync(
        string? credential, IReadOnlyList<AgentRateLimitObservation> readings, Guid? workItemId, CancellationToken ct)
    {
        // Only the subscription has usage windows; an API key run reports none worth a threshold.
        if (credential != AgentCredentials.ClaudeSubscription)
            return;

        // Most readings are under the threshold: one locked write and done.
        var state = await usage.RecordAsync(credential, readings, ct);
        if (CredentialUsageRules.Breach(state, await usage.LoadThresholdsAsync(ct), time.GetUtcNow()) is null)
            return;

        // Decide again under the decision lock: an operator may have resumed since the reading was recorded.
        await using var decision = await usage.LockDecisionAsync(ct);
        if (await BreachAsync(credential, ct) is { } breach)
            await PauseAsync(credential, breach, workItemId, workItemId is { } id ? $"work item {id}" : "a chat", ct);
    }

    private async Task<CredentialUsageBreach?> BreachAsync(string credential, CancellationToken ct) =>
        CredentialUsageRules.Breach(
            await usage.LoadAsync(credential, ct), await usage.LoadThresholdsAsync(ct), time.GetUtcNow());

    private async Task PauseAsync(
        string credential, CredentialUsageBreach breach, Guid? workItemId, string reporter, CancellationToken ct)
    {
        // Every later call of the running agents reports again; a pause that already covers the window changes nothing.
        if (await pauses.LoadAsync(credential, ct) is { } current
            && (current.Source == CredentialPauseSources.Manual || current.Until is null || current.Until >= breach.ResetsAt))
            return;

        var stored = await pauses.PauseAsync(new AgentCredentialPause(
            credential,
            CredentialPauseReasons.UsageThreshold,
            CredentialPauseSources.Auto,
            time.GetUtcNow(),
            breach.ResetsAt,
            breach.Detail,
            WorkItemId: workItemId,
            RunId: workItemId?.ToString()), ct);
        if (stored is not null)
            _log.Information("Usage threshold reached: {PauseDetail}; new work waits until {PausedUntil} (reported by {Reporter})",
                breach.Detail, breach.ResetsAt.ToString("O"), reporter);
    }
}
