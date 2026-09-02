using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

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
        DateTimeOffset? createdAt = null,
        Guid? projectId = null)
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
            CompletedAt = completedAt,
            ProjectId = projectId
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

    /// <summary>
    /// Acceptance criterion: sending the same POST /api/work-items payload twice with the same
    /// RunId results in one DB row and no exception (idempotent retry returns 201).
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_SameRunId_SecondCallReturns201_AndExactlyOneRowExists()
    {
        // Arrange: create a request with a stable RunId so the endpoint derives the same workItemId
        // on both calls (workItemId = Guid.Parse(request.RunId)).
        var runId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"idem-test-{runId:N}"),
            IssueProviderConfigId = "prov-idem",
            RepoProviderConfigId = "repo-idem",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            ProjectId = null,
            RunId = runId.ToString()
        };

        // Act: first POST — must succeed with 201
        var response1 = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response1.StatusCode.Should().Be(HttpStatusCode.Created);
        var returnedId1 = await response1.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);
        returnedId1.Should().Be(runId, "the returned ID must equal the RunId from the request");

        // Act: second POST with identical payload — must also return 201 (idempotent retry)
        var response2 = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response2.StatusCode.Should().Be(HttpStatusCode.Created,
            "a retry with the same RunId must be idempotent and return 201, not 409 or 500");
        var returnedId2 = await response2.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);
        returnedId2.Should().Be(runId, "both calls must return the same work item ID");

        // Assert: exactly one row in the DB — the duplicate was suppressed, not inserted
        using var db = _factory.CreateDbContext();
        var count = db.WorkItems.Count(w => w.Id == runId);
        count.Should().Be(1, "idempotent retry must not create a second DB row");
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
    public async Task GetPendingWorkItems_ProjectIdFilter_ReturnsOnlyMatchingProject()
    {
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var inA = SeedEntity(WorkItemStatus.Pending, projectId: projectA);
        var inB = SeedEntity(WorkItemStatus.Pending, projectId: projectB);

        // The switcher passes the project id as a Guid-string; the endpoint parses it back to the uuid column.
        var response = await _client.GetAsync($"/api/work-items/pending?projectId={projectA}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().Contain(i => i.Id == inA.Id);
        items.Should().NotContain(i => i.Id == inB.Id);
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

    [Fact]
    public async Task GetPendingWorkItems_WithPayload_ProjectsNewFields()
    {
        // Arrange: directly insert a WorkItemEntity with a full Payload containing
        // IssueDetail.Title, InitiatedBy, ProjectName, and ProjectId.
        var issueId = $"display-fields-test-{Guid.NewGuid():N}";
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier(issueId),
            IssueProviderConfigId = "prov-display",
            RepoProviderConfigId = "repo-display",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600,
            ProjectId = new Guid("12300000-0000-0000-0000-000000000001"),
            ProjectName = "Default",
            IssueDetail = new IssueDetail
            {
                Identifier = issueId,
                Title = "My issue title",
                Description = "Some description",
                Labels = []
            }
        };

        using (var db = _factory.CreateDbContext())
        {
            // Seed the Project row first; the FK constraint (added by the migration) requires a
            // matching Projects row when ProjectId is non-null.
            db.Projects.Add(new ProjectEntity
            {
                Id = new Guid("12300000-0000-0000-0000-000000000001"),
                Name = "Default",
                Enabled = true,
                TemplateIds = []
            });
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = issueId,
                IssueProviderConfigId = "prov-display",
                Status = WorkItemStatus.Pending,
                Payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default),
                AgentSelector = "kiro",
                TimeoutSeconds = 3600,
                ProjectId = new Guid("12300000-0000-0000-0000-000000000001"),
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        // Act — use maxResults=500 so the freshly-seeded item is never pushed out of the window
        // by other Pending rows accumulated in the shared integration-test database.
        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        // Assert: the matching DTO has all display fields populated from Payload
        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the seeded work item must appear in /pending");
        dto!.InitiatedBy.Should().Be("loop");
        dto.IssueTitle.Should().Be("My issue title");
        dto.ProjectName.Should().Be("Default");
        dto.ProjectId.Should().Be(new Guid("12300000-0000-0000-0000-000000000001"));
    }

    [Fact]
    public async Task GetPendingWorkItems_WithNullPayload_ReturnsNullForNewFields()
    {
        // Arrange: directly insert a WorkItemEntity with Payload = null.
        // SeedEntity always sets Payload, so we must insert directly here.
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"null-payload-test-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-null",
                Status = WorkItemStatus.Pending,
                Payload = null,   // legacy row — no payload
                AgentSelector = "kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        // Act — use maxResults=500 so the freshly-seeded item is never pushed out of the window
        // by other Pending rows accumulated in the shared integration-test database.
        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        // Assert: new display fields are null (backward-compat for legacy rows)
        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the seeded work item must appear in /pending");
        dto!.IssueTitle.Should().BeNull();
        dto.InitiatedBy.Should().BeNull();
        dto.ProjectName.Should().BeNull();
        dto.ProjectId.Should().BeNull();
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

    // ── RequeueWorkItem — Cancelled→Pending ───────────────────────────────────────

    [Fact]
    public async Task RequeueWorkItem_CancelledToPending_IncrementsRetryCount()
    {
        var entity = SeedEntity(WorkItemStatus.Cancelled);
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.RetryCount.Should().Be(entity.RetryCount + 1);
    }

    // ── GetWorkItemStatus — GET /{id}/status ──────────────────────────────────────

    [Fact]
    public async Task GetWorkItemStatus_Returns200_WithCurrentStatus()
    {
        var entity = SeedEntity(WorkItemStatus.Dispatched);

        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/status");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("status").GetString().Should().Be("Dispatched");
    }

    [Fact]
    public async Task GetWorkItemStatus_Returns404_WhenNotFound()
    {
        var response = await _client.GetAsync($"/api/work-items/{Guid.NewGuid()}/status");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GetK8sJobName — GET /{id}/k8s-job-name ───────────────────────────────────

    [Fact]
    public async Task GetK8sJobName_Returns200_WhenJobNameSet()
    {
        var entity = SeedEntity(WorkItemStatus.Dispatched);

        // Write the K8sJobName directly — not exposed via the creation API
        using (var db = _factory.CreateDbContext())
        {
            var row = await db.WorkItems.FindAsync(entity.Id);
            row!.K8sJobName = "k8s-job-abc123";
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/k8s-job-name");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("jobName").GetString().Should().Be("k8s-job-abc123");
    }

    [Fact]
    public async Task GetK8sJobName_Returns404_WhenJobNameAbsent()
    {
        // SeedEntity does not set K8sJobName — should return 404
        var entity = SeedEntity(WorkItemStatus.Pending);
        var response = await _client.GetAsync($"/api/work-items/{entity.Id}/k8s-job-name");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── GetIsDistributed — recent-terminal path ───────────────────────────────────

    [Fact]
    public async Task GetIsDistributed_ReturnsTrue_WhenRecentlyTerminated()
    {
        // Seed a Succeeded work item completed just now — within dedup cooldown window
        var issueId = $"dist-{Guid.NewGuid():N}";
        var entity = SeedEntity(WorkItemStatus.Succeeded, issueIdentifier: issueId,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await _client.GetAsync(
            $"/api/work-items/is-distributed?issueIdentifier={issueId}&issueProviderConfigId=prov-seed");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("isDistributed").GetBoolean().Should().BeTrue(
            "a recently completed work item should still be considered distributed (dedup window)");
    }

    [Fact]
    public async Task GetIsDistributed_ReturnsFalse_WhenNoMatchingItem()
    {
        var response = await _client.GetAsync(
            $"/api/work-items/is-distributed?issueIdentifier=nonexistent-{Guid.NewGuid():N}&issueProviderConfigId=prov-x");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("isDistributed").GetBoolean().Should().BeFalse();
    }

    // ── GetActiveIdentifiers ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveIdentifiers_IncludesRecentlyTerminatedItems()
    {
        var issueId = $"actid-{Guid.NewGuid():N}";
        // Seed a recently completed item — within the dedup cooldown window
        SeedEntity(WorkItemStatus.Succeeded, issueIdentifier: issueId,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-1));

        var response = await _client.GetAsync("/api/work-items/active-identifiers");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(issueId,
            "recently terminated items must appear in active-identifiers for dedup purposes");
    }

    // ── RequeueWorkItem — Succeeded → 409 ────────────────────────────────────────

    [Fact]
    public async Task RequeueWorkItem_Returns409_WhenSucceeded()
    {
        var entity = SeedEntity(WorkItemStatus.Succeeded);
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        // Succeeded is not Failed or Cancelled — requeue must return 409 Conflict
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    // ── RequeueWorkItem — Dispatched→Pending ─────────────────────────────────────

    /// <summary>
    /// Guards the fix for the CRITICAL finding: when K8s Job creation fails after ClaimAsync
    /// succeeds, SafeRequeueAsync is called on a Dispatched item. Without Dispatched→Pending
    /// support the item would be stuck until EnforceDispatchedTimeoutAsync marks it Failed,
    /// losing the retry entirely.
    /// </summary>
    [Fact]
    public async Task RequeueWorkItem_DispatchedToPending_IncrementsRetryCount()
    {
        var entity = SeedEntity(WorkItemStatus.Dispatched);
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.RetryCount.Should().Be(entity.RetryCount + 1);
        updated.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on requeue from Dispatched");
        updated.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on requeue from Dispatched");
        updated.K8sJobName.Should().BeNull("K8sJobName must be cleared on requeue from Dispatched");
    }

    // ── ClaimWorkItem — ELAPSED bug fix (issue #2106) ─────────────────────────────

    /// <summary>
    /// Regression test for issue #2106: ClaimWorkItem must update the in-memory PipelineRun's
    /// StartedAtOffset to DispatchedAt, not leave it at the enqueue-time UtcNow default.
    /// Uses CreatePendingItemAsync (POST /api/work-items) to materialise the PipelineRun in
    /// IOrchestratorRunService before claiming.
    /// </summary>
    [Fact]
    public async Task ClaimWorkItem_UpdatesInMemoryRunStartedAtOffset_ToDispatchedAt()
    {
        // 1. Create work item via API — also materialises PipelineRun in IOrchestratorRunService
        var id = await CreatePendingItemAsync();

        // Simulate a 15-minute queue wait so dispatchedAt is well before enqueue time
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-15);
        var claim = new ClaimWorkItemRequest
        {
            AssignedAgentId = "agent-1",
            DispatchedAt = dispatchedAt
        };

        // 2. Claim the item
        var response = await _client.PostAsJsonAsync($"/api/work-items/{id}/claim", claim,
            PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // 3. Assert in-memory run was updated to dispatch time, not enqueue time
        var runService = _factory.Services.GetRequiredService<IOrchestratorRunService>();
        var run = runService.GetRun(new RunId(id.ToString()));
        run.Should().NotBeNull("PipelineRun must still be in the run service after claim");
        run!.StartedAtOffset.Should().BeCloseTo(dispatchedAt, TimeSpan.FromSeconds(1),
            "StartedAtOffset must reflect DispatchedAt, not enqueue time");
        // Verify it is NOT enqueue time (enqueue time ≈ UtcNow, not 15 min ago)
        run.StartedAtOffset.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(-14),
            "StartedAtOffset must not be the enqueue-time default (≈ UtcNow)");
    }

    /// <summary>
    /// Regression guard for issue #2106 pod-restart scenario: when the API pod restarts
    /// between CreateWorkItem and ClaimWorkItem, no PipelineRun exists in IOrchestratorRunService.
    /// ClaimWorkItem must not throw in this case — the null-safe guard must be in place.
    /// Uses SeedEntity (direct DB insert) to create the WorkItem without calling CreateWorkItem,
    /// so no PipelineRun is materialised in the run service.
    /// </summary>
    [Fact]
    public async Task ClaimWorkItem_Returns200_WhenNoInMemoryRunExists()
    {
        // Seed DB-only — no in-memory PipelineRun materialised (simulates API pod restart)
        var entity = SeedEntity(WorkItemStatus.Pending);
        var claim = new ClaimWorkItemRequest
        {
            AssignedAgentId = "agent-pod-restart",
            DispatchedAt = DateTimeOffset.UtcNow
        };

        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/claim", claim,
            PipelineJsonOptions.Default);

        // Must not throw — null-safe guard (run is not null) must prevent NRE
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Regression guard for issue #2106: requeueing a claimed item must not interfere with
    /// the claim endpoint or throw. Verifies the requeue path is unaffected by the new
    /// ReplaceRun logic in ClaimWorkItem.
    /// </summary>
    [Fact]
    public async Task RequeueWorkItem_Returns200_AfterClaim()
    {
        // Create, claim, then requeue
        var id = await CreatePendingItemAsync();
        var dispatchedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        var claimResponse = await _client.PostAsJsonAsync($"/api/work-items/{id}/claim",
            new ClaimWorkItemRequest { AssignedAgentId = "agent-1", DispatchedAt = dispatchedAt },
            PipelineJsonOptions.Default);
        claimResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Requeue from Dispatched → Pending
        var requeueResponse = await _client.PostAsync($"/api/work-items/{id}/requeue", null);
        requeueResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify DB state — DispatchedAt nulled, RetryCount incremented
        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on requeue");
        updated.RetryCount.Should().Be(1, "RetryCount must be incremented by requeue");
    }
}
