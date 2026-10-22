using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Api.IntegrationTests.Dispatch;

/// <summary>
/// HTTP integration tests for <c>POST /api/work-items/{id}/dispatch</c>
/// (the claim-by-CAS Scheduler path).
///
/// These tests exercise the endpoint over the full HTTP stack via
/// <see cref="ApiWebApplicationFactory"/> to cover
/// <see cref="WorkItemDispatchEndpoints.DispatchPendingWorkItem"/> and the
/// <see cref="CodingAgent.Api.Dispatch.DispatchWorkItemService.DispatchPendingWorkItemAsync"/>
/// state machine (AC1 and AC2 of issue #3758).
///
/// <para>
/// The seeded item is stored without an agent selector or route, as a previous-release API replica stores a
/// current-release request, and the test environment has no routing table, so the dispatcher's rollout routing
/// (spec 053 DD10) refuses it: <c>200 { dispatched: false, reason: "no_route" }</c> and the item fails. No K8s interaction
/// occurs: the empty <c>KiroPvcPool</c> causes <c>QueryDispatchablePvcsAsync</c>
/// to return early before calling <c>AgentJobPvcs.ListHeldAsync</c>.
/// </para>
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class PendingDispatchHttpTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public PendingDispatchHttpTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a <see cref="WorkItemStatus.Pending"/> work item directly into the shared
    /// InMemory database and returns its ID.
    /// Uses a unique <see cref="WorkItemEntity.IssueIdentifier"/> to avoid cross-test
    /// interference in the shared <see cref="ApiIntegrationTestCollection"/> DB.
    /// </summary>
    private Guid SeedPendingWorkItem()
    {
        using var db = _factory.CreateDbContext();
        // Other tests of the collection may store a routing table; this item must find none.
        db.KeyValueStore.RemoveRange(db.KeyValueStore.Where(kv => kv.Key == CodingAgent.Pipeline.Models.AgentRouting.StorageKey));
        var issueIdentifier = $"dispatch-test-{Guid.NewGuid():N}";
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = "prov-dispatch",
            Status = WorkItemStatus.Pending,
            Payload = JsonSerializer.Serialize(new JobDistributionRequest
            {
                IssueIdentifier = new IssueIdentifier(issueIdentifier),
                IssueProviderConfigId = "prov-dispatch",
                RepoProviderConfigId = "repo-dispatch",
                InitiatedBy = "test",
                TaskType = WorkItemTaskType.Implementation,
                TimeoutSeconds = 3600,
                PayloadSchemaVersion = 1,
            }, PipelineJsonOptions.Default),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkItems.Add(entity);
        db.SaveChanges();
        return entity.Id;
    }

    // ── AC1: Pending item → 200 with dispatched response shape ───────────────

    /// <summary>
    /// Regression test for issue #3758 (AC1).
    /// POST /api/work-items/{id}/dispatch for a seeded Pending work item must return HTTP 200
    /// with a body that deserializes as <see cref="DispatchPendingResponse"/>.
    ///
    /// <para>
    /// The item has no selector and no route resolves for it, so the endpoint returns
    /// <c>{ dispatched: false, reason: "no_route" }</c> and fails the item (spec 053 req 2.9, 4.6).
    /// This exercises the full handler body (<c>WorkItemDispatchEndpoints.DispatchPendingWorkItem</c>
    /// L261-262), the <c>DispatchPendingWorkItemAsync</c> fast-path, lock acquisition, and
    /// preamble (<c>TryEnterSelectorDispatchAsync</c> L725-L799).
    /// </para>
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_PendingItem_Returns200_WithDispatchedResponseShape()
    {
        // Arrange: seed a Pending work item via direct DB access (bypasses POST /api/work-items
        // to keep the test focused on the dispatch endpoint).
        var id = SeedPendingWorkItem();

        // Act: POST with an empty body — the endpoint takes no request body.
        var response = await _client.PostAsync($"/api/work-items/{id}/dispatch", content: null);

        // Assert — HTTP status
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert — response body shape.
        // PipelineJsonOptions.Default has PropertyNamingPolicy=CamelCase, which maps the
        // PascalCase record properties (Dispatched, Reason) to the camelCase JSON the server
        // emits (dispatched, reason).
        var result = await response.Content.ReadFromJsonAsync<DispatchPendingResponse>(
            PipelineJsonOptions.Default);
        result.Should().NotBeNull("the response body must deserialize as DispatchPendingResponse");
        result!.Dispatched.Should().BeFalse("no agent route resolves for the item");
        result.Reason.Should().Be("no_route");

        await using var db = _factory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
        item.Status.Should().Be(WorkItemStatus.Failed);
        item.ErrorMessage.Should().Be(AgentRouteRefusalReason.NoRouteText);
    }

    // ── AC2: Non-existent ID → 404 ────────────────────────────────────────────

    /// <summary>
    /// Regression test for issue #3758 (AC2).
    /// POST /api/work-items/{id}/dispatch for a GUID that has no matching row must
    /// return HTTP 404 Not Found.
    /// </summary>
    [Fact]
    public async Task DispatchPendingWorkItem_NonExistentId_Returns404()
    {
        // Arrange: a random GUID guaranteed not to exist in the DB.
        var nonExistentId = Guid.NewGuid();

        // Act
        var response = await _client.PostAsync($"/api/work-items/{nonExistentId}/dispatch", content: null);

        // Assert
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
