using CodingAgent.Api.Loop;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 051 2.6 (D11): loop control as desired state. Start, Stop and Resume only write the pipeline
/// config and answer 202; the Scheduler leader applies them within a few seconds and pushes its status,
/// which <c>GET /api/ui/loop/status</c> serves. The loop works across projects, so the commands need
/// global operator; the status is open to every user, filtered per D3 rule 5.
/// </summary>
internal static class UiLoopEndpoints
{
    /// <summary>The status message while the Scheduler has not reported recently.</summary>
    internal const string NotReportingMessage = "⏳ Waiting for the Scheduler's status…";

    public static RouteGroupBuilder MapUiLoopEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/loop/status", GetStatus);
        group.MapPost("/loop/start", Start).RequireAuthorization(UiPolicies.GlobalOperate);
        group.MapPost("/loop/stop", Stop).RequireAuthorization(UiPolicies.GlobalOperate);
        group.MapPost("/loop/resume", Resume).RequireAuthorization(UiPolicies.GlobalOperate);
        return group;
    }

    /// <summary>
    /// GET /api/ui/loop/status — the last status the Scheduler leader reported. A project-scoped user
    /// sees only their projects' template statuses, and no messages: they can name other projects'
    /// templates, issues and repositories.
    /// </summary>
    internal static async Task<IResult> GetStatus(
        IUserAccess access, LoopStatusStore store, IPipelineConfigStore config, IProjectStore projects, CancellationToken ct)
    {
        var report = await store.FindAsync();
        var runRequested = (await config.LoadPipelineConfigAsync(ct)).ClosedLoopAutoStart;
        var status = ToUiStatus(report, runRequested);
        if (access.CanRead(null))
            return TypedResults.Ok(status);

        var readable = (await projects.LoadProjectsAsync(ct))
            .Where(p => access.CanRead(p.Id))
            .SelectMany(p => p.TemplateIds)
            .ToHashSet(StringComparer.Ordinal);
        return TypedResults.Ok(status with
        {
            StatusMessage = "",
            CurrentIssueIdentifier = null,
            LastPollError = null,
            ValidationErrors = [],
            TemplateStatuses = status.TemplateStatuses
                .Where(t => readable.Contains(t.Key))
                .ToDictionary(t => t.Key, t => t.Value, StringComparer.Ordinal),
        });
    }

    /// <summary>POST /api/ui/loop/start — asks the Scheduler leader to run the loop.</summary>
    internal static async Task<IResult> Start(IPipelineConfigStore config, CancellationToken ct)
    {
        await config.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = true }, ct);
        return TypedResults.Accepted(default(string));
    }

    /// <summary>POST /api/ui/loop/stop — asks the Scheduler leader to stop the loop after the current run.</summary>
    internal static async Task<IResult> Stop(IPipelineConfigStore config, CancellationToken ct)
    {
        await config.UpdatePipelineConfigAsync(c => c with { ClosedLoopAutoStart = false }, ct);
        return TypedResults.Accepted(default(string));
    }

    /// <summary>POST /api/ui/loop/resume — asks the Scheduler leader to resume a loop its circuit breaker paused.</summary>
    internal static async Task<IResult> Resume(IPipelineConfigStore config, TimeProvider time, CancellationToken ct)
    {
        var requestedAt = time.GetUtcNow();
        await config.UpdatePipelineConfigAsync(c => c with { LoopResumeRequestedAt = requestedAt }, ct);
        return TypedResults.Accepted(default(string));
    }

    private static UiLoopStatus ToUiStatus(LoopStatusReport? report, bool runRequested)
    {
        if (report is null)
            return UiLoopStatus.NotReported(runRequested, NotReportingMessage);

        var s = report.Status;
        return new UiLoopStatus(
            s.IsLoopActive, runRequested, s.StatusMessage, s.CurrentIssueIdentifier,
            s.ProcessedCount, s.FailedCount, s.QueueCount, s.IsCircuitBroken, s.LastPollError,
            s.CurrentCycleTemplateIndex, s.CurrentCycleTemplateCount,
            s.ValidationErrors, s.TemplateStatuses, report.ReportedAt);
    }
}
