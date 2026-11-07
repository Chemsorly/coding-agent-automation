using System.Diagnostics;
using System.Text.Json;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.Dispatch;

/// <summary>What <see cref="DispatchRouter.RouteAsync"/> found for a Pending item.</summary>
public enum DispatchRouteStatus
{
    /// <summary>The route resolved, its job template exists and its credential is available.</summary>
    Routed,

    /// <summary>The item cannot start now; the hold is recorded on it.</summary>
    Held,

    /// <summary>The item is not Pending (any more).</summary>
    NotPending,

    /// <summary>No such work item.</summary>
    NotFound,
}

/// <summary>The outcome of <see cref="DispatchRouter.RouteAsync"/>.</summary>
/// <param name="Status">What the router found.</param>
/// <param name="TemplateKey">The resolved job template key (normalized); null when no route resolved.</param>
/// <param name="Credential">The resolved credential; null when no route resolved or no tool is known.</param>
/// <param name="Template">The job template to dispatch under; set for <see cref="DispatchRouteStatus.Routed"/>.</param>
/// <param name="HoldReason">The hold reason (<see cref="HoldReasons"/>); set for <see cref="DispatchRouteStatus.Held"/>.</param>
/// <param name="HoldText">The hold text; set for <see cref="DispatchRouteStatus.Held"/>.</param>
public sealed record DispatchRouteOutcome(
    DispatchRouteStatus Status,
    string? TemplateKey = null,
    string? Credential = null,
    JobTemplate? Template = null,
    string? HoldReason = null,
    string? HoldText = null);

/// <summary>
/// Spec 056 DD2, DD12 (replaces spec 053's creation-time router and its D7): routes a Pending work item when it is
/// dispatched, before the agent start gate (D14). Resolution order is spec 053's (repository row, global task row,
/// global default); nothing picks a profile no route names (D4: no automatic fallback). An item that cannot start now is
/// held with its reason and text: <c>no_route</c> (also for an unreadable stored request), <c>profile_unavailable</c>,
/// <c>no_tool</c>, <c>no_template</c>, <c>credential_paused</c>. A resolved route is pinned on the item (selector,
/// credential, payload route and profile), also when the credential is paused, so the Scheduler's loop can skip by it.
/// </summary>
public sealed class DispatchRouter(
    IDbContextFactory<PipelineDbContext> dbFactory,
    IAgentProfileStore profiles,
    AgentRoutingStore routing,
    IProjectStore templates,
    JobTemplateStore jobTemplates,
    CredentialPauseStore pauses,
    IOrchestratorRunService runs,
    TimeProvider time,
    Serilog.ILogger logger)
{
    private readonly Serilog.ILogger _log = LogStartup(logger.ForContext<DispatchRouter>(), jobTemplates);

    /// <summary>N14: a host without job templates knows no tools, so no credential is derived and no pause applies.</summary>
    private static Serilog.ILogger LogStartup(Serilog.ILogger log, JobTemplateStore jobTemplates)
    {
        if (jobTemplates.ToolsByKey().Count == 0)
            log.Warning("No job templates are configured: credentials cannot be derived, so credential pauses do not hold work");
        return log;
    }

    /// <summary>
    /// Routes the Pending item <paramref name="id"/> and records the result on it in one save: the pinned route, or the
    /// hold. Only values that changed are written, so a held item costs no write per cycle.
    /// </summary>
    public async Task<DispatchRouteOutcome> RouteAsync(Guid id, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (entity is null)
            return new DispatchRouteOutcome(DispatchRouteStatus.NotFound);
        if (entity.Status != WorkItemStatus.Pending)
            return new DispatchRouteOutcome(DispatchRouteStatus.NotPending);

        var now = time.GetUtcNow();
        DispatchRouteOutcome outcome;
        if (!WorkItemPayload.TryDeserialize(entity.Payload, out var request) || request is null)
        {
            outcome = new DispatchRouteOutcome(DispatchRouteStatus.Held,
                HoldReason: HoldReasons.NoRoute, HoldText: HoldTexts.UnreadableRequest);
        }
        else
        {
            var resolution = await ResolveAsync(request, ct);
            if (resolution.Route is { } route)
                PinRoute(entity, request, route, resolution.TemplateKey!, resolution.Credential);
            outcome = resolution.Outcome;
            // A hold text can carry configured names (a job template key, a profile name): no raw line breaks.
            if (outcome.HoldText is { } text)
                outcome = outcome with { HoldText = LogSanitizer.SanitizeForLog(text) };
        }

        if (outcome.Status == DispatchRouteStatus.Held)
            WorkItemHolds.Set(entity, outcome.HoldReason!, outcome.HoldText!, now);

        try
        {
            if (db.ChangeTracker.HasChanges())
                await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Another dispatcher moved the item meanwhile; the next cycle reads it again.
            return new DispatchRouteOutcome(DispatchRouteStatus.NotPending);
        }

        Annotate(outcome);
        if (outcome.Status == DispatchRouteStatus.Held)
        {
            _log.Debug("Work item {WorkItemId} ({IssueIdentifier}) held: {HoldReason} — {HoldText}",
                id, LogSanitizer.SanitizeForLog(entity.IssueIdentifier), outcome.HoldReason, outcome.HoldText);
        }
        return outcome;
    }

    /// <summary>
    /// Spec 056 DD12: what dispatch would do with <paramref name="request"/> now, without writing anything: the hold
    /// text, or null when it would start (limits aside).
    /// </summary>
    public async Task<string?> PreviewAsync(JobDistributionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolution = await ResolveAsync(request, ct);
        return resolution.Outcome.Status == DispatchRouteStatus.Held
            ? LogSanitizer.SanitizeForLog(resolution.Outcome.HoldText)
            : null;
    }

    /// <summary>The result of resolving a request: the outcome, and the route to pin when one resolved.</summary>
    private sealed record Resolution(DispatchRouteOutcome Outcome, AgentRouteResult? Route, string? TemplateKey, string? Credential);

    private async Task<Resolution> ResolveAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var template = await OwningTemplateAsync(request, ct);
        var table = await routing.LoadAsync(ct) ?? AgentRouting.Empty;
        var allProfiles = await profiles.LoadAgentProfilesAsync(ct);
        var toolsByKey = jobTemplates.ToolsByKey();

        var result = AgentRouteResolver.Resolve(request.TaskType, template, table, allProfiles, toolsByKey);
        if (result.IsRefused)
        {
            var text = result.RefusalReason == AgentRouteRefusalReason.NoRoute
                ? HoldTexts.NoRoute(request.TaskType)
                : result.RefusalText!;
            return new Resolution(
                new DispatchRouteOutcome(DispatchRouteStatus.Held, HoldReason: result.RefusalReason, HoldText: text),
                null, null, null);
        }

        var profile = result.Profile!;
        var templateKey = profile.JobTemplateKey();
        var credential = toolsByKey.Count == 0 ? null : AgentCredentials.For(profile, toolsByKey);
        var jobTemplate = jobTemplates.Resolve(JobTemplateStore.NormalizeLabels(templateKey));
        if (jobTemplate is null)
        {
            return new Resolution(
                new DispatchRouteOutcome(DispatchRouteStatus.Held, templateKey, credential,
                    HoldReason: HoldReasons.NoTemplate, HoldText: HoldTexts.NoTemplate(templateKey, profile.DisplayName)),
                result, templateKey, credential);
        }

        if (credential is not null && await pauses.LoadAsync(credential, ct) is { } pause)
        {
            return new Resolution(
                new DispatchRouteOutcome(DispatchRouteStatus.Held, templateKey, credential,
                    HoldReason: HoldReasons.CredentialPaused, HoldText: HoldTexts.CredentialPaused(pause)),
                result, templateKey, credential);
        }

        return new Resolution(
            new DispatchRouteOutcome(DispatchRouteStatus.Routed, templateKey, credential, jobTemplate),
            result, templateKey, credential);
    }

    /// <summary>
    /// Writes the resolved route on the item when it differs from the stored one (req 3.7: a route pinned by an earlier
    /// release is replaced), and on a new route updates the in-memory run's profile, model and effort (S2).
    /// </summary>
    private void PinRoute(
        WorkItemEntity entity, JobDistributionRequest request, AgentRouteResult route, string templateKey, string? credential)
    {
        var selector = JobTemplateStore.NormalizeLabels(templateKey);
        if (!string.Equals(entity.AgentSelector, selector, StringComparison.Ordinal))
            entity.AgentSelector = selector;
        if (!string.Equals(entity.Credential, credential, StringComparison.Ordinal))
            entity.Credential = credential;

        var snapshot = route.Snapshot!;
        if (request.Route == snapshot
            && string.Equals(request.ResolvedProfileId, route.Profile!.Id, StringComparison.Ordinal)
            && string.Equals(request.AgentSelector, templateKey, StringComparison.Ordinal))
        {
            return;
        }

        var routed = request with { Route = snapshot, ResolvedProfileId = route.Profile!.Id, AgentSelector = templateKey };
        entity.Payload = JsonSerializer.Serialize(WorkItemEntityBuilder.BuildMinimalPayload(routed), PipelineJsonOptions.Default);

        var routeSource = AgentRouteLabels.Source(snapshot.Source);
        _log.Information(
            "Routed {TaskType} work item {WorkItemId} to {AgentProfile} · {Model} · {Effort} ({RouteSource}; job template {AgentSelector}, credential {Credential})",
            request.TaskType, entity.Id, snapshot.ProfileName, snapshot.Model, snapshot.Effort, routeSource,
            LogSanitizer.SanitizeForLog(selector), credential);
        PipelineTelemetry.SetRouteAttributes(Activity.Current, snapshot.ProfileName, snapshot.Model, snapshot.Effort, routeSource);

        if (runs.GetRun(new RunId(entity.Id.ToString())) is { } run)
        {
            PipelineRunFactory.ApplyRoute(run, snapshot);
            runs.ReplaceRun(run);
        }
    }

    private static void Annotate(DispatchRouteOutcome outcome)
    {
        var activity = Activity.Current;
        if (activity is null)
            return;
        if (outcome.Credential is not null)
            activity.SetTag(PipelineTelemetry.CredentialAttributes.Credential, outcome.Credential);
        if (outcome.Status == DispatchRouteStatus.Held)
        {
            activity.SetTag(PipelineTelemetry.CredentialAttributes.HoldReason, outcome.HoldReason);
            activity.SetTag(PipelineTelemetry.CredentialAttributes.HoldText, outcome.HoldText);
        }
    }

    /// <summary>
    /// Spec 053 req 4.3: the template that triggered a consolidation; for every other task type the template that owns
    /// the work item's repository, the enabled one first. Null applies the global rows only.
    /// </summary>
    private async Task<PipelineJobTemplate?> OwningTemplateAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var all = await templates.LoadAllTemplatesAsync(ct);
        return request.TaskType == WorkItemTaskType.Consolidation
            ? all.FirstOrDefault(t => string.Equals(t.Id, request.ConsolidationTemplateId, StringComparison.Ordinal))
            : RepositorySettings.TemplateFor(request.RepoProviderConfigId, all);
    }
}

/// <summary>
/// Spec 056 DD7: the hold record of a Pending work item. Written only when the reason or the text changes;
/// <see cref="WorkItemEntity.HeldAt"/> stays while the same reason repeats.
/// </summary>
internal static class WorkItemHolds
{
    /// <summary>Records the hold on the tracked <paramref name="entity"/>; returns false when nothing changed.</summary>
    public static bool Set(WorkItemEntity entity, string reason, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var cut = HoldTexts.Cut(text);
        if (string.Equals(entity.HoldReason, reason, StringComparison.Ordinal)
            && string.Equals(entity.HoldText, cut, StringComparison.Ordinal))
            return false;
        if (!string.Equals(entity.HoldReason, reason, StringComparison.Ordinal) || entity.HeldAt is null)
            entity.HeldAt = now;
        entity.HoldReason = reason;
        entity.HoldText = cut;
        return true;
    }

    /// <summary>Clears the hold of the tracked <paramref name="entity"/> (it was dispatched).</summary>
    public static void Clear(WorkItemEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.HoldReason is null && entity.HoldText is null && entity.HeldAt is null)
            return;
        entity.HoldReason = null;
        entity.HoldText = null;
        entity.HeldAt = null;
    }

    /// <summary>
    /// Records a hold found inside the agent start gate (spec 057 limits, the PVC gate) on item <paramref name="id"/>
    /// through <paramref name="db"/>. A concurrent change of the item is ignored: the next cycle records it again.
    /// </summary>
    public static async Task SetAsync(PipelineDbContext db, Guid id, string reason, string text, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entity = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (entity is null || entity.Status != WorkItemStatus.Pending || !Set(entity, reason, text, now))
            return;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The item left Pending or was held by another replica; nothing to record.
        }
    }
}
