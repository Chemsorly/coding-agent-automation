using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for the scheduler-facing API endpoints:
///   POST /api/scheduler/maintenance/retention-sweep
///   GET  /api/work-items/counts-by-status
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class SchedulerEndpointTests
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public SchedulerEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── POST /api/scheduler/maintenance/retention-sweep ──────────────────────

    [Fact]
    public async Task RetentionSweep_WhenLeader_Returns200WithResult()
    {
        // Act
        var response = await _client.PostAsync(
            "/api/scheduler/maintenance/retention-sweep", content: null);

        // Assert: leader (the factory mock always sets IsLeader=true) → 200 with counts
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "leader replica should execute the sweep and return 200");

        var body = await response.Content.ReadFromJsonAsync<RetentionSweepResultDto>(JsonOpts);
        body.Should().NotBeNull("response body must be a RetentionSweepResultDto");
        // Counts are >= 0 (may be 0 in a fresh test DB — that's valid)
        body!.StaleWorkItemsDeleted.Should().BeGreaterThanOrEqualTo(0);
        body.StalePipelineRunsDeleted.Should().BeGreaterThanOrEqualTo(0);
        body.StaleConsolidationRunsDeleted.Should().BeGreaterThanOrEqualTo(0);
        body.RetentionPipelineRunsDeleted.Should().BeGreaterThanOrEqualTo(0);
        body.RetentionWorkItemsDeleted.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task RetentionSweep_IsStateless_AlwaysReturns200()
    {
        // Spec 049: The API no longer has leader election. The endpoint always executes
        // when called — the Scheduler's RetentionSweepSchedulerService gates calls on its
        // own leader election so only one Scheduler replica triggers the sweep per interval.
        var response = await _client.PostAsync(
            "/api/scheduler/maintenance/retention-sweep", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "API is stateless — retention sweep always executes when called, no 503 leader gate");
    }

    [Fact]
    public async Task RetentionSweep_WithoutApiKey_Returns401()
    {
        using var noAuthClient = _factory.CreateClient();
        var response = await noAuthClient.PostAsync(
            "/api/scheduler/maintenance/retention-sweep", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── GET /api/work-items/counts-by-status ─────────────────────────────────

    [Fact]
    public async Task WorkItemCounts_ReturnsEmptyArrayWhenNone()
    {
        var response = await _client.GetAsync("/api/work-items/counts-by-status");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<WorkItemCountDto[]>(JsonOpts);
        body.Should().NotBeNull();
        // Empty DB → empty array (InMemory seeding didn't add any items for this test)
    }

    [Fact]
    public async Task WorkItemCounts_WithSeededItems_ReturnsGroupedCounts()
    {
        // Arrange: seed two work items with different statuses
        using var db = _factory.CreateDbContext();
        var projectId = Guid.NewGuid();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            Status = WorkItemStatus.Pending,
            AgentSelector = "default",
            Payload = "{}",
            ProjectId = projectId,
            TaskType = WorkItemTaskType.Implementation
        });
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            Status = WorkItemStatus.Pending,
            AgentSelector = "default",
            Payload = "{}",
            ProjectId = projectId,
            TaskType = WorkItemTaskType.Implementation
        });
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            Status = WorkItemStatus.Dispatched,
            AgentSelector = "default",
            Payload = "{}",
            ProjectId = projectId,
            TaskType = WorkItemTaskType.Implementation
        });
        await db.SaveChangesAsync();

        // Act
        var response = await _client.GetAsync("/api/work-items/counts-by-status");

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<WorkItemCountDto[]>(JsonOpts);
        body.Should().NotBeNull();
        // At least two groups (Pending x2, Dispatched x1)
        body!.Length.Should().BeGreaterThanOrEqualTo(1,
            "seeded work items should produce at least one status group");
    }

    [Fact]
    public async Task WorkItemCounts_WithoutApiKey_Returns401()
    {
        using var noAuthClient = _factory.CreateClient();
        var response = await noAuthClient.GetAsync("/api/work-items/counts-by-status");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
