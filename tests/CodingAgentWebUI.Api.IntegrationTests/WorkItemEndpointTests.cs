using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// xUnit collection marker — all test classes sharing ApiWebApplicationFactory
/// are placed in this collection so the factory is created ONCE for the entire
/// collection, not once per test class. This avoids the "logger already frozen"
/// issue caused by multiple host builds freezing the same global Serilog logger.
/// </summary>
[CollectionDefinition(Name)]
public sealed class ApiIntegrationTestCollection : ICollectionFixture<ApiWebApplicationFactory>
{
    public const string Name = "ApiIntegrationTests";
}

/// <summary>
/// Integration tests for /api/work-items endpoints.
/// Uses InMemory EF Core — concurrency tests (Req 4.5c) are deferred to
/// CodingAgentWebUI.Infrastructure.IntegrationTests since EF InMemory cannot
/// exercise xmin row-version tokens.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class WorkItemEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public WorkItemEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static JobDistributionRequest MakeRequest(string? issueIdentifier = null) => new()
    {
        IssueIdentifier = new IssueIdentifier(issueIdentifier ?? $"issue-{Guid.NewGuid():N}"),
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "",
        TimeoutSeconds = 3600,
        ProjectId = null
    };

    private async Task<Guid> CreatePendingItemAsync(string? issueIdentifier = null)
    {
        var response = await _client.PostAsJsonAsync("/api/work-items", MakeRequest(issueIdentifier),
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);
        return id;
    }

    private WorkItemEntity SeedEntity(WorkItemStatus status, string? issueIdentifier = null,
        WorkItemTaskType taskType = WorkItemTaskType.Implementation,
        FailureReason? failureReason = null,
        DateTimeOffset? completedAt = null,
        DateTimeOffset? createdAt = null)
    {
        using var db = _factory.CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = taskType,
            IssueIdentifier = issueIdentifier ?? $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-seed",
            Status = status,
            Payload = JsonSerializer.Serialize(MakeRequest(issueIdentifier), PipelineJsonOptions.Default),
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            FailureReason = failureReason,
            CompletedAt = completedAt
        };
        db.WorkItems.Add(entity);
        db.SaveChanges();
        return entity;
    }

    // ── Assignment ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignment_Returns200_WithExpectedFields()
    {
        var id = await CreatePendingItemAsync();

        // Advance to Dispatched so assignment is valid
        using (var db = _factory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(id);
            item!.Status = WorkItemStatus.Dispatched;
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/work-items/{id}/assignment");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(id.ToString());
    }

    [Fact]
    public async Task GetAssignment_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/work-items/{Guid.NewGuid()}/assignment");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAssignment_Returns404_WhenPayloadIsNull()
    {
        using var db = _factory.CreateDbContext();
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Dispatched,
            Payload = null,   // null payload
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();

        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/assignment");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task GetAssignment_Returns410_ForTerminalStatus(WorkItemStatus status)
    {
        var entity = SeedEntity(status);
        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/assignment");
        response.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    // ── Status ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostStatus_DispatchedToRunning_Returns200()
    {
        var entity = SeedEntity(WorkItemStatus.Dispatched);
        var update = new { status = "Running" };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostStatus_RunningToSucceeded_Returns200()
    {
        var entity = SeedEntity(WorkItemStatus.Running);
        var update = new { status = "Succeeded" };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostStatus_InvalidTransition_Returns400()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        var update = new { status = "Succeeded" };  // Pending→Succeeded is invalid
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task PostStatus_FailedWithReason_PersistsFailureReason()
    {
        var entity = SeedEntity(WorkItemStatus.Running);
        var update = new
        {
            status = "Failed",
            failureReason = "AgentError",
            errorMessage = "agent crashed"
        };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.FailureReason.Should().Be(FailureReason.AgentError);
        updated.ErrorMessage.Should().Be("agent crashed");
    }

    [Theory]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task PostStatus_TerminalTransition_SetsCompletedAt(WorkItemStatus terminal)
    {
        var fromStatus = terminal == WorkItemStatus.Succeeded ? WorkItemStatus.Running
            : terminal == WorkItemStatus.Failed ? WorkItemStatus.Running
            : WorkItemStatus.Running;

        var entity = SeedEntity(fromStatus);
        var update = new { status = terminal.ToString() };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.CompletedAt.Should().NotBeNull();
    }

    // ── Claim ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimWorkItem_Returns200_WhenPending()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        var claim = new { assignedAgentId = "agent-1", dispatchedAt = DateTimeOffset.UtcNow };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/claim", claim,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// HTTP contract test: 409 when TransitionIfAsync returns false.
    /// Tests the endpoint's status mapping, not actual concurrency.
    /// Real concurrency (Req 4.5c) is deferred to Infrastructure.IntegrationTests
    /// where a live Postgres instance is available.
    /// </summary>
    [Fact]
    public async Task ClaimWorkItem_Returns409_WhenAlreadyDispatched()
    {
        // Seed as Dispatched — TransitionIfAsync returns false because item.Status != Pending
        var entity = SeedEntity(WorkItemStatus.Dispatched);
        var claim = new { assignedAgentId = "agent-2", dispatchedAt = DateTimeOffset.UtcNow };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/claim", claim,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task ClaimWorkItem_Returns404_WhenNotFound()
    {
        var claim = new { assignedAgentId = "agent-x", dispatchedAt = DateTimeOffset.UtcNow };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{Guid.NewGuid()}/claim", claim,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Create ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateWorkItem_Returns201_WithGuid()
    {
        var request = MakeRequest();
        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);
        id.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task CreateWorkItem_AppearsInPending()
    {
        var issue = $"create-test-{Guid.NewGuid():N}";
        var id = await CreatePendingItemAsync(issue);

        var pendingResponse = await _client.GetAsync("/api/work-items/pending");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var pendingBody = await pendingResponse.Content.ReadAsStringAsync();
        pendingBody.Should().Contain(id.ToString());
    }

    [Fact]
    public async Task CreateWorkItem_Returns400_WhenMissingRequiredFields()
    {
        var response = await _client.PostAsJsonAsync("/api/work-items", new { }, PipelineJsonOptions.Default);
        // Missing required fields → 400 or 422
        ((int)response.StatusCode).Should().BeGreaterThanOrEqualTo(400);
    }

    // ── Pending ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingWorkItems_OrderedByCreatedAtAsc()
    {
        var base_ = DateTimeOffset.UtcNow;
        var older = SeedEntity(WorkItemStatus.Pending, createdAt: base_.AddMinutes(-5));
        var newer = SeedEntity(WorkItemStatus.Pending, createdAt: base_.AddMinutes(-1));

        var response = await _client.GetAsync("/api/work-items/pending");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        var olderIndex = items!.FindIndex(i => i.Id == older.Id);
        var newerIndex = items.FindIndex(i => i.Id == newer.Id);

        if (olderIndex >= 0 && newerIndex >= 0)
            olderIndex.Should().BeLessThan(newerIndex);
    }

    [Fact]
    public async Task GetPendingWorkItems_ExcludesConsolidation()
    {
        var consolidation = SeedEntity(WorkItemStatus.Pending, taskType: WorkItemTaskType.Consolidation);

        var response = await _client.GetAsync("/api/work-items/pending");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().NotContain(i => i.Id == consolidation.Id);
    }

    [Fact]
    public async Task GetPendingWorkItems_RespectsMaxResults()
    {
        // Seed 5 pending items
        for (var i = 0; i < 5; i++)
            SeedEntity(WorkItemStatus.Pending);

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=2");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Count.Should().BeLessThanOrEqualTo(2);
    }

    // ── Requeue ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RequeueWorkItem_FailedToPending_IncrementsRetryCount()
    {
        var entity = SeedEntity(WorkItemStatus.Failed);
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.RetryCount.Should().Be(entity.RetryCount + 1);
    }

    [Fact]
    public async Task RequeueWorkItem_Returns409_WhenPending()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task RequeueWorkItem_Returns404_WhenNotFound()
    {
        var response = await _client.PostAsync($"/api/work-items/{Guid.NewGuid()}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── RetryCount ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCount_Returns0_ForNewItem()
    {
        var id = await CreatePendingItemAsync();
        var response = await _client.GetAsync($"/api/work-items/{id}/retry-count");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("retryCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task GetRetryCount_IncrementsAfterRequeue()
    {
        var entity = SeedEntity(WorkItemStatus.Failed);
        await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);

        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/retry-count");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("retryCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetRetryCount_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/work-items/{Guid.NewGuid()}/retry-count");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Staleness ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStaleness_ReturnsCorrectFlags()
    {
        var issueId = $"stale-{Guid.NewGuid():N}";
        var provId = "prov-stale";
        var since = DateTimeOffset.UtcNow.AddHours(-1);

        // Seed a Failed(AgentError) item and a Succeeded item
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = issueId,
                IssueProviderConfigId = provId,
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.AgentError,
                CompletedAt = DateTimeOffset.UtcNow,
                Payload = null,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = issueId,
                IssueProviderConfigId = provId,
                Status = WorkItemStatus.Succeeded,
                CompletedAt = DateTimeOffset.UtcNow,
                Payload = null,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var url = $"/api/work-items/staleness?issueIdentifier={issueId}&issueProviderConfigId={provId}&since={Uri.EscapeDataString(since.ToString("O"))}";
        var response = await _client.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<WorkItemStalenessResult>(PipelineJsonOptions.Default);
        result.Should().NotBeNull();
        result!.HasAgentErrorSince.Should().BeTrue();
        result.LastSuccessfulCompletion.Should().NotBeNull();
    }

    // ── Active ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveWorkItems_ReturnsDispatchedAndRunning_OlderThanThreshold()
    {
        var dispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-300);
        var dispatched = SeedEntity(WorkItemStatus.Dispatched);
        var running = SeedEntity(WorkItemStatus.Running);
        var recentlyDispatched = SeedEntity(WorkItemStatus.Dispatched);

        // Manually set DispatchedAt to control timing
        using (var db = _factory.CreateDbContext())
        {
            var d = await db.WorkItems.FindAsync(dispatched.Id);
            d!.DispatchedAt = dispatchedAt;
            var r = await db.WorkItems.FindAsync(running.Id);
            r!.DispatchedAt = dispatchedAt;
            var rd = await db.WorkItems.FindAsync(recentlyDispatched.Id);
            rd!.DispatchedAt = DateTimeOffset.UtcNow; // recent — should NOT appear
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=60");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().Contain(i => i.Id == dispatched.Id);
        items.Should().Contain(i => i.Id == running.Id);
        items.Should().NotContain(i => i.Id == recentlyDispatched.Id);
    }

    [Fact]
    public async Task GetActiveWorkItems_DoesNotReturnPendingOrTerminal()
    {
        var pending = SeedEntity(WorkItemStatus.Pending);
        var succeeded = SeedEntity(WorkItemStatus.Succeeded);
        var failed = SeedEntity(WorkItemStatus.Failed);

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().NotContain(i => i.Id == pending.Id);
        items.Should().NotContain(i => i.Id == succeeded.Id);
        items.Should().NotContain(i => i.Id == failed.Id);
    }

    // ── LabelSwap ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostLabelSwap_Returns200_WhenWorkItemExists()
    {
        // ILabelSwapService is registered but ILabelService is a mock with no configured provider.
        // The endpoint's ILabelSwapService? nullable injection: endpoint returns 200 even if
        // LabelSwapService is null. Integration tests mock IProviderFactory, so swap is skipped.
        var entity = SeedEntity(WorkItemStatus.Dispatched);
        var body = new { label = "agent:in-progress" };

        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/label-swap", body,
            PipelineJsonOptions.Default);

        // 200 is expected — the handler degrades gracefully when ILabelSwapService cannot
        // complete the swap (no configured provider factory in integration tests).
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task PostLabelSwap_Returns404_WhenWorkItemNotFound()
    {
        var body = new { label = "agent:in-progress" };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{Guid.NewGuid()}/label-swap", body,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── LastProgress ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostLastProgress_Returns200_AndUpdatesField()
    {
        var entity = SeedEntity(WorkItemStatus.Running);
        var progressTime = DateTimeOffset.UtcNow.AddMinutes(-5);
        var body = new { timestamp = progressTime };

        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/last-progress", body,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.LastProgressAt.Should().NotBeNull();
        // Allow ±1s tolerance for serialization rounding
        updated.LastProgressAt!.Value.Should().BeCloseTo(progressTime, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task PostLastProgress_Returns404_WhenWorkItemNotFound()
    {
        var body = new { timestamp = DateTimeOffset.UtcNow };
        var response = await _client.PostAsJsonAsync($"/api/work-items/{Guid.NewGuid()}/last-progress", body,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
