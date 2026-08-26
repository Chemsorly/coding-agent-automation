using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Api;

/// <summary>
/// Minimal API endpoints for consolidation work item dispatch.
/// Called by the Job Controller's ConsolidationDispatchLoop — mirrors the /api/work-items
/// endpoints but scoped to TaskType=Consolidation and includes server-side payload enrichment
/// (provider config resolution + token vending) so the JC stays stateless and EF-free.
/// All endpoints require Operator-tier authentication (master key).
/// </summary>
public static class ConsolidationWorkItemEndpoints
{
    public static void MapConsolidationWorkItemEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/consolidation-work-items")
            .RequireAuthorization(ApiAuthPolicies.Operator);

        group.MapGet("/pending", GetPendingConsolidationWorkItems);
        group.MapPost("/{id:guid}/claim", ClaimConsolidationWorkItem);
    }

    // ── GET /pending ──────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/consolidation-work-items/pending
    /// Returns Pending consolidation WorkItems (TaskType=Consolidation), ordered by CreatedAt ASC.
    /// Does NOT enrich payloads here — enrichment happens at claim time so tokens are as
    /// fresh as possible. Query param: maxResults (default 50).
    /// </summary>
    internal static async Task<IResult> GetPendingConsolidationWorkItems(
        IDbContextFactory<PipelineDbContext> dbFactory,
        int maxResults = 50,
        CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var items = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.Status == WorkItemStatus.Pending
                     && w.TaskType == WorkItemTaskType.Consolidation)
            .OrderBy(w => w.CreatedAt)
            .Take(maxResults)
            .Select(w => new PendingWorkItemDto
            {
                Id = w.Id,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType,
                CreatedAt = w.CreatedAt,
                AgentSelector = w.AgentSelector,
                RetryCount = w.RetryCount
            })
            .ToListAsync(ct);

        return TypedResults.Ok((IReadOnlyList<PendingWorkItemDto>)items);
    }

    // ── POST /{id}/claim ──────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/consolidation-work-items/{id}/claim
    /// Atomic Pending → Dispatched CAS. Enriches the payload inline (resolves provider configs,
    /// vends short-lived tokens) before writing, then returns the enriched payload to the JC.
    /// This keeps token vending server-side and tokens as fresh as possible (at claim time).
    /// Returns 200 ConsolidationWorkItemClaimResponse, 409 already claimed, 404 not found,
    /// 422 if payload enrichment fails (JC should requeue).
    /// </summary>
    internal static async Task<IResult> ClaimConsolidationWorkItem(
        Guid id,
        [FromBody] ClaimWorkItemRequest request,
        WorkItemTransitionService transitionService,
        IDbContextFactory<PipelineDbContext> dbFactory,
        IConsolidationJobPreparationService consolidationJobPreparer,
        IPipelineConfigStore? pipelineConfigStore,
        IProjectStore? projectStore,
        IConfiguration configuration,
        CancellationToken ct)
    {
        // Step 1: load the work item to get its payload before claiming
        await using var db = await dbFactory.CreateDbContextAsync(ct);
        var entity = await db.WorkItems
            .Where(w => w.Id == id && w.Status == WorkItemStatus.Pending)
            .FirstOrDefaultAsync(ct);

        if (entity is null)
        {
            var exists = await db.WorkItems.AnyAsync(w => w.Id == id, ct);
            return exists
                ? TypedResults.Conflict("Work item is not in Pending state or was already claimed.")
                : TypedResults.NotFound();
        }

        // Step 2: enrich payload (resolve provider configs, vend tokens)
        ConsolidationWorkItemClaimPayload? enriched;
        try
        {
            enriched = await EnrichPayloadAsync(entity, consolidationJobPreparer, pipelineConfigStore, projectStore, ct);
        }
        catch (Exception ex)
        {
            Serilog.Log.ForContext("SourceContext", nameof(ConsolidationWorkItemEndpoints))
                .Error(ex, "ConsolidationWorkItemEndpoints: payload enrichment failed for WorkItem {WorkItemId}", id);
            // 422 Unprocessable — caller should fail/requeue the item
            return TypedResults.UnprocessableEntity($"Payload enrichment failed: {ex.Message}");
        }

        if (enriched is null)
            return TypedResults.UnprocessableEntity("WorkItem has no valid consolidation payload.");

        // Step 3: atomic Pending → Dispatched CAS, writing enriched payload
        var success = await transitionService.TransitionIfAsync(
            id,
            expectedCurrent: WorkItemStatus.Pending,
            target: WorkItemStatus.Dispatched,
            mutate: wi =>
            {
                wi.AssignedAgentId = request.AssignedAgentId;
                wi.DispatchedAt = request.DispatchedAt;
                if (request.K8sJobName is not null)
                    wi.K8sJobName = request.K8sJobName;
                wi.Payload = enriched.EnrichedPayloadJson;
            },
            ct: ct);

        if (!success)
        {
            // Race: another replica claimed between our load and this CAS — return 409
            return TypedResults.Conflict("Work item is not in Pending state or was already claimed.");
        }

        var orchestratorUrl =
            configuration.GetValue<string>("WorkDistribution:OrchestratorUrl")
            ?? configuration.GetValue<string>("OrchestratorUrl")
            ?? "";

        return TypedResults.Ok(new ConsolidationWorkItemClaimResponse
        {
            WorkItemId = id,
            RunId = entity.IssueIdentifier ?? id.ToString(),
            EnrichedPayloadJson = enriched.EnrichedPayloadJson,
            ProjectSecrets = enriched.ProjectSecrets,
            OrchestratorUrl = orchestratorUrl
        });
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private sealed record ConsolidationWorkItemClaimPayload(
        string EnrichedPayloadJson,
        Dictionary<string, string>? ProjectSecrets);

    private static async Task<ConsolidationWorkItemClaimPayload?> EnrichPayloadAsync(
        Infrastructure.Persistence.Entities.WorkItemEntity entity,
        IConsolidationJobPreparationService preparer,
        IPipelineConfigStore? pipelineConfigStore,
        IProjectStore? projectStore,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(entity.Payload))
            return null;

        JobDistributionRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<JobDistributionRequest>(entity.Payload, PipelineJsonOptions.Default);
        }
        catch (JsonException)
        {
            return null;
        }

        if (request is null)
            return null;

        // Parse agent labels from selector string
        var agentLabels = (entity.AgentSelector ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList()
            .AsReadOnly();

        var preparation = await preparer.PrepareAsync(
            request.ConsolidationRunType ?? ConsolidationRunType.BrainConsolidation,
            string.IsNullOrEmpty(request.ConsolidationTemplateId) ? (TemplateId?)null : (TemplateId)request.ConsolidationTemplateId,
            agentLabels,
            ct);

        Pipeline.Models.PipelineConfiguration? pipelineConfig = null;
        if (pipelineConfigStore is not null)
            pipelineConfig = await pipelineConfigStore.LoadPipelineConfigAsync(ct);

        var enrichedRequest = request with
        {
            ProviderConfigs = preparation.ProviderConfigs ?? [],
            RepoProviderConfigId = preparation.RepoProviderConfigId,
            PipelineConfiguration = pipelineConfig ?? new Pipeline.Models.PipelineConfiguration()
        };

        var enrichedJson = JsonSerializer.Serialize(enrichedRequest, PipelineJsonOptions.Default);

        // Resolve project secrets
        Dictionary<string, string>? projectSecrets = null;
        var projectId = entity.ProjectId;
        if (string.IsNullOrEmpty(projectId) && projectStore is not null
            && !string.IsNullOrEmpty(request.ConsolidationTemplateId))
        {
            var projects = await projectStore.LoadProjectsAsync(ct);
            var owner = projects?.FirstOrDefault(p =>
                p.Enabled && p.TemplateIds.Contains(request.ConsolidationTemplateId));
            projectId = owner?.Id;
        }

        if (!string.IsNullOrEmpty(projectId) && projectStore is not null)
        {
            var project = await projectStore.GetProjectByIdAsync(projectId, ct);
            if (project?.Secrets is { Count: > 0 })
                projectSecrets = project.Secrets;
        }

        return new ConsolidationWorkItemClaimPayload(enrichedJson, projectSecrets);
    }
}
