using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Tests for <c>POST /api/work-items/dispatch</c> endpoint (issue #2322).
/// Verifies the synchronous dispatch path acceptance criteria.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class DispatchEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public DispatchEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private static JobDistributionRequest MakeRequest(string? issueId = null) => new()
    {
        IssueIdentifier = new IssueIdentifier(issueId ?? $"dispatch-test-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-dispatch",
        RepoProviderConfigId = "repo-dispatch",
        InitiatedBy = "dispatch-test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,dotnet",
        TimeoutSeconds = 3600
    };

    // ── AC1: No Pending rows on live dispatch path ─────────────────────────────────

    /// <summary>
    /// AC1: Verifies that <c>POST /api/work-items/dispatch</c> never leaves a WorkItem
    /// with <c>Status=Pending</c> as the final state.
    /// The endpoint either (a) dispatches synchronously → Dispatched, or
    /// (b) fails gracefully → Failed/503, or (c) returns 503 before writing any row.
    /// </summary>
    [Fact]
    public async Task PostDispatch_NeverLeavesWorkItemAsPending()
    {
        var issueId = $"ac1-dispatch-{Guid.NewGuid():N}";
        var request = MakeRequest(issueId);

        var response = await _client.PostAsJsonAsync("/api/work-items/dispatch", request,
            PipelineJsonOptions.Default);

        // Endpoint returns 200, 409, or 503 — all acceptable (no K8s in test environment)
        response.StatusCode.Should().BeOneOf(
            HttpStatusCode.OK,
            HttpStatusCode.Conflict,
            HttpStatusCode.ServiceUnavailable);

        // If any row was written, it must NOT be in Pending status
        await using var db = _factory.CreateDbContext();
        var item = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.IssueIdentifier == issueId)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync();

        if (item is not null)
        {
            item.Status.Should().NotBe(WorkItemStatus.Pending,
                $"POST /api/work-items/dispatch must never leave a WorkItem in Pending status. " +
                $"WorkItem {item.Id} has Status={item.Status}");
        }
    }

    // ── AC4: PVC double-assignment — two concurrent calls serialize via _pvcSelectLock ──

    /// <summary>
    /// AC4 (endpoint-level smoke test): Two concurrent calls to <c>POST /api/work-items/dispatch</c>
    /// must not produce HTTP 500 errors and must not leave duplicate <c>ClaimedPvcName</c> entries
    /// for the same PVC in the DB.
    ///
    /// In this integration environment the PVC pool is empty and K8s is unavailable, so both
    /// requests return 503 before any <c>ClaimedPvcName</c> is written — confirming neither
    /// request crashes mid-lifecycle and the uniqueness assertion holds vacuously.
    ///
    /// The deterministic lock guarantee (exactly one 200 / one 503 with a real PVC pool) is
    /// exercised in <c>DispatchLifecycleServicePvcLockTests.SelectPvcFromDbAsync_WhenPoolHasOnePvc_ConcurrentCallersGetExactlyOneSuccessAndOneNull</c>,
    /// which uses a real <see cref="CodingAgentWebUI.Api.Dispatch.DispatchLifecycleService"/>
    /// instance with a seeded PVC pool and pre-inserted WorkItem rows.
    /// </summary>
    [Fact]
    public async Task PostDispatch_ConcurrentRequests_NeitherCrashesAndNoClaimedPvcDuplicate()
    {
        // Two different issues — concurrent calls for different work items
        var issueId1 = $"ac4-concurrent-a-{Guid.NewGuid():N}";
        var issueId2 = $"ac4-concurrent-b-{Guid.NewGuid():N}";

        // Fire both concurrently
        var task1 = _client.PostAsJsonAsync("/api/work-items/dispatch", MakeRequest(issueId1), PipelineJsonOptions.Default);
        var task2 = _client.PostAsJsonAsync("/api/work-items/dispatch", MakeRequest(issueId2), PipelineJsonOptions.Default);

        var responses = await Task.WhenAll(task1, task2);

        // Both must return a valid HTTP status — NOT 500 (which would indicate a crash mid-lifecycle)
        foreach (var response in responses)
        {
            // a concurrent dispatch call must never return 500; the lifecycle must handle all error paths gracefully
            response.StatusCode.Should().BeOneOf(
                HttpStatusCode.OK,
                HttpStatusCode.Conflict,
                HttpStatusCode.ServiceUnavailable);
        }

        // Verify no duplicate PVC assignments in the DB (ClaimedPvcName should be unique across live items)
        await using var db = _factory.CreateDbContext();
        var claimedPvcs = await db.WorkItems
            .AsNoTracking()
            .Where(w => w.ClaimedPvcName != null &&
                        (w.IssueIdentifier == issueId1 || w.IssueIdentifier == issueId2))
            .Select(w => w.ClaimedPvcName!)
            .ToListAsync();

        // No PVC should appear twice across the two concurrent dispatches.
        // In the no-K8s/no-PVC environment this list will be empty (503 returned before claim is written),
        // which is still a valid result. The actual lock guarantee is tested in DispatchLifecycleServicePvcLockTests.
        claimedPvcs.Should().OnlyHaveUniqueItems(
            "SelectPvcFromDbAsync writes ClaimedPvcName to the DB inside _pvcSelectLock, preventing double-assignment");
    }

    // ── Recovery path — requeue endpoint still works after dispatch path change ─────

    /// <summary>
    /// AC5 partial: Verifies that <c>POST /api/work-items/{id}/requeue</c> still transitions
    /// a Failed WorkItem to Pending (recovery path is unaffected by the dispatch path change).
    /// </summary>
    [Fact]
    public async Task RequeueWorkItem_StillTransitionsFailedToPending()
    {
        // Seed a Failed work item directly
        await using var db = _factory.CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"requeue-ac5-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-requeue",
            Status = WorkItemStatus.Failed,
            Payload = "{}",
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            FailureReason = FailureReason.AgentError,
            CompletedAt = DateTimeOffset.UtcNow
        };
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();

        // Act: requeue the failed item
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify it transitioned to Pending
        await using var verifyDb = _factory.CreateDbContext();
        var updated = await verifyDb.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending,
            "Failed → Pending requeue path must still function after dispatch path change (issue #2322)");
    }
}
