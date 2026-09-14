using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Integration tests for GET /api/work-items/pending with
/// <c>Consolidation:UnifiedDispatch:Enabled=true</c>.
///
/// These tests require the flag to be on, so they use <see cref="UnifiedDispatchWebApplicationFactory"/>
/// which is isolated in its own collection to avoid cross-contamination with the shared
/// <see cref="ApiIntegrationTestCollection"/> factory.
/// </summary>
[Collection("UnifiedDispatchCollection")]
public sealed class WorkItemEndpointUnifiedDispatchTests
{
    private readonly UnifiedDispatchWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WorkItemEndpointUnifiedDispatchTests(UnifiedDispatchWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private WorkItemEntity SeedEntity(WorkItemStatus status, WorkItemTaskType taskType = WorkItemTaskType.Implementation)
    {
        // TODO: The shared InMemory database (one factory instance per collection) is never cleared
        // between tests — rows seeded here accumulate across test runs in the same process. Currently
        // only one test exists so Guid-keyed assertions hide the issue, but any future test that
        // checks total count or ordering may see unexpected rows from prior seeds. Consider a
        // per-test db-name strategy (e.g. Guid suffix per test) if the collection grows.
        using var db = _factory.CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = taskType,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-seed",
            Status = status,
            Payload = JsonSerializer.Serialize(new JobDistributionRequest
            {
                IssueIdentifier = new IssueIdentifier($"issue-{Guid.NewGuid():N}"),
                IssueProviderConfigId = "prov-seed",
                RepoProviderConfigId = "repo-seed",
                InitiatedBy = "test",
                TaskType = taskType,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                ProjectId = null
            }, PipelineJsonOptions.Default),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkItems.Add(entity);
        db.SaveChanges();
        return entity;
    }

    /// <summary>
    /// Acceptance criterion: when <c>Consolidation:UnifiedDispatch:Enabled=true</c>,
    /// a consolidation <c>Pending</c> item IS returned by <c>GET /api/work-items/pending</c>
    /// so the <c>WorkItemDispatchPoller</c> can pick it up.
    /// </summary>
    [Fact]
    public async Task GetPendingWorkItems_IncludesConsolidation_WhenFlagOn()
    {
        // Arrange: seed a consolidation Pending item
        var consolidation = SeedEntity(WorkItemStatus.Pending, taskType: WorkItemTaskType.Consolidation);

        // Act
        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        // Assert: the consolidation item is present when the flag is on
        // TODO: Add a non-consolidation Pending item to the seed and assert it is also returned,
        // to verify the base Status==Pending filter still works correctly with the flag on.
        // TODO: Add a consolidation item with non-Pending status (e.g. InProgress) and assert it
        // is NOT returned, to verify the Status predicate half of the relaxed filter is enforced.
        items!.Should().Contain(i => i.Id == consolidation.Id,
            "when Consolidation:UnifiedDispatch:Enabled=true, consolidation Pending items must be " +
            "returned by GET /api/work-items/pending so the WorkItemDispatchPoller can dispatch them");
    }
}
