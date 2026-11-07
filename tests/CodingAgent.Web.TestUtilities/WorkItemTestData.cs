using System.Text.Json;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Spec 056 (task 4.1, B5): Pending work items that dispatch can route. The dispatcher routes every Pending item from
/// its stored request (design DD2); an item whose payload cannot be read is held as <c>no_route</c>, so a test that
/// dispatches an item stores a readable request.
/// </summary>
public static class WorkItemTestData
{
    /// <summary>
    /// The stored payload of a minimal readable request (the identity-only form the API stores), for an item of
    /// <paramref name="taskType"/> on issue <paramref name="issueIdentifier"/>.
    /// </summary>
    public static string RoutablePayload(
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        string issueIdentifier = "1",
        string issueProviderConfigId = "issue-provider",
        string repoProviderConfigId = "repo-provider",
        string? consolidationTemplateId = null) =>
        JsonSerializer.Serialize(new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier(issueIdentifier),
            IssueProviderConfigId = issueProviderConfigId,
            RepoProviderConfigId = repoProviderConfigId,
            InitiatedBy = InitiatedByConstants.LoopIssue,
            TaskType = taskType,
            RunType = taskType switch
            {
                WorkItemTaskType.Review => PipelineRunType.Review,
                WorkItemTaskType.Triage => PipelineRunType.Triage,
                WorkItemTaskType.Consolidation => PipelineRunType.Consolidation,
                WorkItemTaskType.Decomposition => PipelineRunType.Decomposition,
                _ => PipelineRunType.Implementation,
            },
            TimeoutSeconds = 3600,
            ConsolidationTemplateId = consolidationTemplateId,
            PayloadSchemaVersion = 1,
        }, PipelineJsonOptions.Default);

    /// <summary>
    /// Stores the global routing table (by default every task type to <see cref="AgentRoutingTestData.Profile"/> on
    /// <see cref="AgentRoutingTestData.DefaultJobTemplate"/>) and a Pending work item with a readable request.
    /// <paramref name="configure"/> sets further fields before the insert. The caller's host must know the routed
    /// profile (its <c>IAgentProfileStore</c>) and job template.
    /// </summary>
    public static async Task<WorkItemEntity> SeedRoutedPendingAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        Action<WorkItemEntity>? configure = null,
        AgentRouting? routing = null,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        await AgentRoutingTestData.SeedRoutingAsync(dbFactory, routing, ct);

        var id = Guid.NewGuid();
        var entity = new WorkItemEntity
        {
            Id = id,
            TaskType = taskType,
            IssueIdentifier = $"seed-{id:N}",
            IssueProviderConfigId = "issue-provider",
            Status = WorkItemStatus.Pending,
            Payload = RoutablePayload(taskType, $"seed-{id:N}"),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        configure?.Invoke(entity);

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync(ct);
        return entity;
    }
}
