using System.Diagnostics;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Api.Dispatch;

/// <summary>The outcome of <see cref="WorkItemRouter.RouteAsync"/>.</summary>
/// <param name="Request">The request with its route pinned; null for a refusal.</param>
/// <param name="RefusalReason">One of <see cref="AgentRouteRefusalReason"/>; null when routed.</param>
/// <param name="RefusalText">The text the refusing surface shows; null when routed.</param>
public sealed record WorkItemRouteResult(JobDistributionRequest? Request, string? RefusalReason, string? RefusalText)
{
    public bool IsRefused => Request is null;

    public static WorkItemRouteResult Refused(string reason, string text) => new(null, reason, text);
}

/// <summary>
/// Spec 053 DD1, req 4.1–4.3, 4.10: resolves a work item's route once, in the API, when it is created, and pins it on
/// the request: <see cref="JobDistributionRequest.Route"/>, the agent selector (the profile's job template key) and the
/// resolved profile Id. Callers send the task type and the repository; they never resolve profiles. Every refusal
/// carries its reason (D15); nothing picks a profile no route names.
/// </summary>
public sealed class WorkItemRouter(
    IAgentProfileStore profiles,
    AgentRoutingStore routing,
    IProjectStore templates,
    JobTemplateStore jobTemplates,
    ILogger<WorkItemRouter> logger)
{
    /// <param name="request">The request to route.</param>
    /// <param name="stage">The <see cref="PipelineTelemetry.RoutingStages"/> value a refusal is counted under.</param>
    /// <param name="source">Overrides the snapshot's source (the dispatcher's rollout routing records <c>Dispatch</c>).</param>
    public async Task<WorkItemRouteResult> RouteAsync(
        JobDistributionRequest request,
        string stage,
        CancellationToken ct,
        AgentRouteSource? source = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = await OwningTemplateAsync(request, ct);
        var table = await routing.LoadAsync(ct) ?? AgentRouting.Empty;
        var allProfiles = await profiles.LoadAgentProfilesAsync(ct);
        var toolsByKey = jobTemplates.ToolsByKey();
        var templateName = template?.Name ?? "no template";

        var result = AgentRouteResolver.Resolve(request.TaskType, template, table, allProfiles, toolsByKey);
        if (!result.IsRefused && request.TaskType == WorkItemTaskType.Consolidation
            && toolsByKey.Count > 0 && !toolsByKey.ContainsKey(result.Profile!.JobTemplateKey()))
        {
            // Consolidation is refused at creation when its template is missing (#3642): the dispatcher could
            // never start it, and the Consolidation page enqueues in-process, so the check lives here.
            result = AgentRouteResult.Refused(
                AgentRouteRefusalReason.TemplateMissing,
                $"Job template {result.Profile.JobTemplateKey()} of agent profile '{result.Profile.DisplayName}' does not exist in the Helm values");
        }

        if (result.IsRefused)
        {
            logger.LogWarning(
                "Refused {TaskType} work for {TemplateName}: {RefusalText} ({RefusalReason}, template {TemplateId})",
                request.TaskType, templateName, result.RefusalText, result.RefusalReason, template?.Id);
            PipelineTelemetry.RecordRoutingRefusal(request.TaskType, stage, result.RefusalReason!);
            PipelineTelemetry.SetRouteRefusal(Activity.Current, result.RefusalReason!, result.RefusalText!);
            return WorkItemRouteResult.Refused(result.RefusalReason!, result.RefusalText!);
        }

        var snapshot = source is { } overridden ? result.Snapshot! with { Source = overridden } : result.Snapshot!;
        var routeSource = AgentRouteLabels.Source(snapshot.Source);
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Routed {TaskType} work for {TemplateName} to {AgentProfile} · {Model} · {Effort} ({RouteSource}; profile {AgentProfileId}, template {TemplateId})",
                request.TaskType, templateName, snapshot.ProfileName, snapshot.Model, snapshot.Effort, routeSource,
                snapshot.ProfileId, template?.Id);
        }
        PipelineTelemetry.SetRouteAttributes(Activity.Current, snapshot.ProfileName, snapshot.Model, snapshot.Effort, routeSource);

        return new WorkItemRouteResult(
            request with
            {
                Route = snapshot,
                AgentSelector = result.Profile!.JobTemplateKey(),
                ResolvedProfileId = result.Profile.Id,
            },
            null,
            null);
    }

    /// <summary>
    /// Req 4.3: the template that triggered a consolidation; for every other task type the template that owns the work
    /// item's repository, the enabled one first (<see cref="RepositorySettings.TemplateFor"/>). Null applies the
    /// global rows only.
    /// </summary>
    private async Task<PipelineJobTemplate?> OwningTemplateAsync(JobDistributionRequest request, CancellationToken ct)
    {
        var all = await templates.LoadAllTemplatesAsync(ct);
        var template = request.TaskType == WorkItemTaskType.Consolidation
            ? all.FirstOrDefault(t => string.Equals(t.Id, request.ConsolidationTemplateId, StringComparison.Ordinal))
            : RepositorySettings.TemplateFor(request.RepoProviderConfigId, all);
        if (template is null && logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "{TaskType} work for issue {IssueIdentifier} has no template (repository {RepoProviderConfigId}, consolidation template {ConsolidationTemplateId}); only the global routes apply",
                request.TaskType, request.IssueIdentifier, request.RepoProviderConfigId, request.ConsolidationTemplateId);
        }
        return template;
    }
}
