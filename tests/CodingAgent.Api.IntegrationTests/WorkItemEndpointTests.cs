using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Api.IntegrationTests;

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
/// CodingAgent.Infrastructure.IntegrationTests since EF InMemory cannot
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

    /// <summary>
    /// Regression test for issue #2202 Fix C (primary).
    /// PostStatus with failureReason="Timeout" must return 200 and persist FailureReason.Timeout.
    /// The fix ensures EmitTerminalStatusTelemetryAsync parses request.FailureReason and passes it
    /// to LogTerminalStatus instead of null, so workdistribution_workitems_terminated emits
    /// failure_reason="Timeout" instead of "none".
    /// </summary>
    [Fact]
    public async Task PostStatus_FailedWithTimeoutReason_Returns200AndPersistsFailureReason()
    {
        var entity = SeedEntity(WorkItemStatus.Running);
        var update = new
        {
            status = "Failed",
            failureReason = "Timeout",
            errorMessage = "agent timed out"
        };

        var response = await _client.PostAsJsonAsync($"/api/work-items/{entity.Id}/status", update,
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.FailureReason.Should().Be(FailureReason.Timeout,
            "FailureReason=Timeout must be persisted so the metric tag is accurate");
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

    /// <summary>
    /// Regression guard for issue #2338: when a kiro agent claims a work item, the
    /// <c>KiroPvcName</c> from the request must be persisted to <c>ClaimedPvcName</c>
    /// on the <see cref="WorkItemEntity"/>, so that <c>GET /api/agents/credential-pool</c>
    /// can compute accurate available-PVC counts from the database.
    ///
    /// Without this fix the <c>ClaimedPvcName</c> column remained null for Job Controller-
    /// dispatched items, causing the credential pool tile to always report <c>4/4</c>
    /// (all available) even when all 4 slots were consumed.
    /// </summary>
    [Fact]
    public async Task ClaimWorkItem_WithKiroPvcName_PersistsClaimedPvcNameToEntity()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        var claim = new ClaimWorkItemRequest
        {
            AssignedAgentId = "kiro-job-1",
            K8sJobName = "kiro-job-1",
            DispatchedAt = DateTimeOffset.UtcNow,
            KiroPvcName = "kiro-creds-pvc-1"
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/claim", claim, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated.Should().NotBeNull();
        updated!.ClaimedPvcName.Should().Be("kiro-creds-pvc-1",
            "KiroPvcName from the claim request must be persisted to ClaimedPvcName so the " +
            "credential pool endpoint can report accurate availability (issue #2338)");
    }

    /// <summary>
    /// Non-kiro agents do not supply a <c>KiroPvcName</c>; <c>ClaimedPvcName</c> must remain
    /// null so they do not pollute the PVC availability query (issue #2338).
    /// </summary>
    [Fact]
    public async Task ClaimWorkItem_WithoutKiroPvcName_LeavesClaimedPvcNameNull()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        var claim = new ClaimWorkItemRequest
        {
            AssignedAgentId = "opencode-job-1",
            K8sJobName = "opencode-job-1",
            DispatchedAt = DateTimeOffset.UtcNow
            // KiroPvcName intentionally omitted — non-kiro agent
        };

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/claim", claim, PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated.Should().NotBeNull();
        updated!.ClaimedPvcName.Should().BeNull(
            "non-kiro agents must not set ClaimedPvcName; PVC availability query is kiro-only");
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

    /// <summary>
    /// Characterization test for the Dispatched→Pending path in <c>RequeueWorkItem</c>.
    /// Verifies that: (1) the item transitions to Pending, (2) RetryCount is incremented,
    /// and (3) <c>K8sJobName</c> is cleared to null (the Dispatched-specific mutation).
    ///
    /// <para>
    /// This test is the safety net for the AC3 loop refactoring. After collapsing the three
    /// sequential <c>TransitionIfAsync</c> blocks into a loop, this test locks in the
    /// conditional <c>K8sJobName = null</c> mutation that only applies to the Dispatched→Pending
    /// path (the Failed and Cancelled paths do not clear <c>K8sJobName</c>).
    /// </para>
    /// </summary>
    [Fact]
    public async Task RequeueWorkItem_DispatchedToPending_IncrementsRetryCount_ClearsK8sJobName()
    {
        // Arrange: seed a Dispatched item with a non-null K8sJobName and a known RetryCount.
        var entity = SeedEntity(WorkItemStatus.Dispatched);
        using (var db = _factory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(entity.Id);
            item!.K8sJobName = "k8s-job-abc123";
            item.RetryCount = 2;
            await db.SaveChangesAsync();
        }

        // Act
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Assert
        using var verifyDb = _factory.CreateDbContext();
        var updated = await verifyDb.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending, "Dispatched→Pending transition must succeed");
        updated.RetryCount.Should().Be(3, "RetryCount must be incremented from 2 to 3");
        updated.K8sJobName.Should().BeNull("K8sJobName must be cleared to null on Dispatched→Pending requeue");
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

    [Fact]
    public async Task GetActiveWorkItems_ReturnsIssueTitle_WhenPayloadHasIssueDetail()
    {
        // Seed a Dispatched work item whose Payload contains a JobDistributionRequest with IssueDetail.
        var entity = SeedEntity(WorkItemStatus.Dispatched);

        // Overwrite the Payload with one that includes IssueDetail so we can verify IssueTitle extraction.
        var requestWithTitle = MakeRequest(entity.IssueIdentifier) with
        {
            IssueDetail = new IssueDetail
            {
                Description = "A test issue",
                Identifier = entity.IssueIdentifier,
                Labels = [],
                Title = "My test issue title"
            }
        };
        using (var db = _factory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(entity.Id);
            item!.Payload = JsonSerializer.Serialize(requestWithTitle, PipelineJsonOptions.Default);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        var seeded = items!.FirstOrDefault(i => i.Id == entity.Id);
        seeded.Should().NotBeNull("the seeded active item must appear in the response");
        seeded!.IssueTitle.Should().Be("My test issue title",
            "IssueTitle must be extracted from JobDistributionRequest.IssueDetail.Title in the Payload");
    }

    [Fact]
    public async Task GetActiveWorkItems_ReturnsNullIssueTitle_WhenPayloadLacksIssueDetail()
    {
        // SeedEntity uses MakeRequest which sets IssueDetail = null — so IssueTitle must be null.
        var entity = SeedEntity(WorkItemStatus.Running);

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        var seeded = items!.FirstOrDefault(i => i.Id == entity.Id);
        seeded.Should().NotBeNull("the seeded active item must appear in the response");
        seeded!.IssueTitle.Should().BeNull(
            "IssueTitle must be null when the Payload JobDistributionRequest has no IssueDetail");
    }

    [Fact]
    public async Task GetActiveWorkItems_ReturnsInitiatedBy_WhenPayloadHasIt()
    {
        // Seed a Running work item, then overwrite its Payload with InitiatedBy = "loop:issue"
        // (overriding the default "test" set by MakeRequest()).
        var entity = SeedEntity(WorkItemStatus.Running);

        var requestWithInitiatedBy = MakeRequest(entity.IssueIdentifier) with
        {
            InitiatedBy = "loop:issue"
        };
        using (var db = _factory.CreateDbContext())
        {
            var item = await db.WorkItems.FindAsync(entity.Id);
            item!.Payload = JsonSerializer.Serialize(requestWithInitiatedBy, PipelineJsonOptions.Default);
            await db.SaveChangesAsync();
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        var seeded = items!.FirstOrDefault(i => i.Id == entity.Id);
        seeded.Should().NotBeNull("the seeded active item must appear in the response");
        seeded!.InitiatedBy.Should().Be("loop:issue",
            "InitiatedBy must be extracted from JobDistributionRequest.InitiatedBy in the Payload");
    }

    [Fact]
    public async Task GetActiveWorkItems_ReturnsNullInitiatedByAndIssueTitle_WhenPayloadIsNull()
    {
        // SeedEntity always writes a non-null Payload — construct the entity directly so we can
        // set Payload = null. DispatchedAt must be in the past so the item passes olderThanSeconds=0.
        // Satisfies the TODO at ~line 824 which requested a null-Payload test.
        Guid entityId;
        using (var db = _factory.CreateDbContext())
        {
            var entity = new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-seed",
                Status = WorkItemStatus.Running,
                Payload = null,
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-300),
                DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-300)
            };
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync();
            entityId = entity.Id;
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        var seeded = items!.FirstOrDefault(i => i.Id == entityId);
        seeded.Should().NotBeNull("the seeded active item with null Payload must appear in the response");
        seeded!.InitiatedBy.Should().BeNull(
            "InitiatedBy must be null when the work item has no Payload");
        seeded.IssueTitle.Should().BeNull(
            "IssueTitle must be null when the work item has no Payload");
    }

    // TODO: [WARNING] The corrupt-payload (JsonException) branch in GetActiveWorkItems is not covered by
    // any test. Add a test that inserts a WorkItemEntity with Status=Running and Payload set to an invalid
    // JSON string (e.g. Payload = "{invalid"), calls GET /api/work-items/active?olderThanSeconds=0, and
    // asserts HTTP 200 with InitiatedBy == null and IssueTitle == null for that entity. This validates the
    // catch (JsonException) guard and ensures a regression that removes it would be caught.

    /// <summary>
    /// The SQL fallback <c>|| (w.DispatchedAt == null &amp;&amp; w.CreatedAt &lt; cutoff)</c>
    /// in GetActiveWorkItems must return a Running item with null DispatchedAt once its
    /// CreatedAt exceeds the olderThanSeconds cutoff.
    /// Covers the untested 1C-001 path added to guard against NULL &lt; cutoff being falsy in SQL.
    /// Also verifies that CreatedAt is included in the response DTO.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_NullDispatchedAt_OlderThanCutoff_IsReturnedWithCreatedAt()
    {
        var createdAt = DateTimeOffset.UtcNow.AddSeconds(-300);
        Guid entityId;
        // TODO [WARNING]: synchronous `using` wrapping an async operation — if the scheduler yields
        // inside SaveChangesAsync the context may be disposed mid-operation, causing an
        // ObjectDisposedException. Change to `await using var db = _factory.CreateDbContext();`
        // (or the block form `await using (var db = ...)`) to ensure async disposal.
        // (DotNetSpecialist review [WARNING])
        using (var db = _factory.CreateDbContext())
        {
            var entity = new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-seed",
                Status = WorkItemStatus.Running,
                Payload = JsonSerializer.Serialize(MakeRequest(), PipelineJsonOptions.Default),
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = createdAt,
                DispatchedAt = null // never written — the bug scenario
            };
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync();
            entityId = entity.Id;
        }

        // olderThanSeconds=60: CreatedAt is 300s old, so it should be returned via the SQL fallback
        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=60");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        var found = items!.FirstOrDefault(i => i.Id == entityId);
        found.Should().NotBeNull(
            "a null-DispatchedAt Running item with CreatedAt older than the cutoff must be returned " +
            "via the SQL fallback (w.DispatchedAt == null && w.CreatedAt < cutoff)");
        found!.DispatchedAt.Should().BeNull("DispatchedAt was not set");
        found.CreatedAt.Should().NotBeNull("CreatedAt must be projected into ActiveWorkItemDto");
        found.CreatedAt!.Value.Should().BeCloseTo(createdAt, TimeSpan.FromSeconds(2),
            "CreatedAt in the DTO must match the entity's CreatedAt");
    }

    /// <summary>
    /// A null-DispatchedAt item whose CreatedAt is within the olderThanSeconds cutoff
    /// must NOT be returned — it has not yet aged out of the grace period at the SQL layer.
    /// </summary>
    [Fact]
    public async Task GetActiveWorkItems_NullDispatchedAt_WithinCutoff_IsNotReturned()
    {
        Guid entityId;
        // TODO [WARNING]: synchronous `using` wrapping an async operation — same IAsyncDisposable
        // mismatch as GetActiveWorkItems_NullDispatchedAt_OlderThanCutoff_IsReturnedWithCreatedAt above.
        // Change to `await using var db = _factory.CreateDbContext();` to avoid potential
        // ObjectDisposedException if the context is disposed while SaveChangesAsync is in-flight.
        // (DotNetSpecialist review [WARNING])
        using (var db = _factory.CreateDbContext())
        {
            var entity = new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-seed",
                Status = WorkItemStatus.Running,
                Payload = JsonSerializer.Serialize(MakeRequest(), PipelineJsonOptions.Default),
                AgentSelector = "",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow, // just created — within any reasonable cutoff
                DispatchedAt = null
            };
            db.WorkItems.Add(entity);
            await db.SaveChangesAsync();
            entityId = entity.Id;
        }

        // olderThanSeconds=60: CreatedAt is ~0s old, so it should NOT be returned
        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=60");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().NotContain(i => i.Id == entityId,
            "a null-DispatchedAt item whose CreatedAt is within the olderThanSeconds cutoff " +
            "must not be returned — it is not yet eligible for grace-window escalation");
    }

    // ── LabelSwap ─────────────────────────────────────────────────────────────────

    // TODO [WARNING]: PostLabelSwap is missing characterization tests for the WorkItemPayload.TryDeserialize
    // branch introduced in Issue #2776. The existing test below seeds a non-Review (Dispatched/Implementation)
    // work item, so isReview=false and the TryDeserialize branch is never entered. Add tests for:
    //   1. Review-type work item with a PascalCase payload — verifies providerConfigIdValue resolves
    //      to RepoProviderConfigId (the silent no-op regression this fix addressed).
    //   2. Review-type work item with a malformed payload — verifies graceful fallback to IssueProviderConfigId
    //      rather than an error (mirrors the malformed-payload tests added for the other three call sites).
    // Without these tests, reverting PostLabelSwap back to PipelineJsonOptions.Default would not be caught.
    // (Issue #2776)
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

    /// <summary>
    /// Covers the active-status branch of the combined single-query predicate introduced to
    /// eliminate the TOCTOU window. Before the fix, GetIsDistributed used two sequential
    /// AnyAsync round-trips: (1) active-status check, (2) recently-terminal check. A WorkItem
    /// that was active when the first query ran but transitioned to terminal before the second
    /// query ran could cause both queries to return false and the endpoint to report
    /// isDistributed = false (spurious 409 on re-dispatch). The fix merges both conditions into
    /// a single AnyAsync call so both halves of the OR are evaluated at the same DB snapshot.
    ///
    /// This test verifies the active-status branch: a Running item (CompletedAt = null) must
    /// return isDistributed = true via the activeStatuses.Contains(w.Status) clause, confirming
    /// the single combined predicate correctly handles in-flight items without requiring
    /// CompletedAt to be set.
    /// </summary>
    [Fact]
    public async Task GetIsDistributed_ReturnsTrue_WhenActiveItemHasNullCompletedAt()
    {
        // Seed a Running item with no CompletedAt — represents an in-flight WorkItem.
        // The combined predicate must catch this row via the active-status branch
        // (activeStatuses.Contains(w.Status)), not the recently-terminal branch.
        var issueId = $"toctou-active-{Guid.NewGuid():N}";
        SeedEntity(WorkItemStatus.Running, issueIdentifier: issueId, completedAt: null);

        var response = await _client.GetAsync(
            $"/api/work-items/is-distributed?issueIdentifier={issueId}&issueProviderConfigId=prov-seed");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("isDistributed").GetBoolean().Should().BeTrue(
            "an in-flight WorkItem (Running, CompletedAt=null) must be considered distributed " +
            "by the active-status branch of the combined single-query predicate");
    }

    /// <summary>
    /// Verifies the TOCTOU fix: both the active-status branch AND the recently-terminal branch
    /// of the combined single-query predicate are evaluated in one round-trip. When a WorkItem
    /// has just transitioned to terminal (CompletedAt set, within dedup cooldown) it must be
    /// caught by the recently-terminal branch.
    /// </summary>
    [Fact]
    public async Task GetIsDistributed_ReturnsTrue_WhenJustTransitionedToTerminal()
    {
        // Seed a Cancelled item with CompletedAt = just now, representing the state immediately
        // after a terminal transition. The combined single-query predicate must catch this row via
        // the recently-terminal branch (CompletedAt != null && CompletedAt >= cutoff).
        var issueId = $"toctou-terminal-{Guid.NewGuid():N}";
        SeedEntity(WorkItemStatus.Cancelled, issueIdentifier: issueId,
            completedAt: DateTimeOffset.UtcNow);

        var response = await _client.GetAsync(
            $"/api/work-items/is-distributed?issueIdentifier={issueId}&issueProviderConfigId=prov-seed");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("isDistributed").GetBoolean().Should().BeTrue(
            "a WorkItem that just transitioned to terminal (CompletedAt within cooldown) " +
            "must be considered distributed by the combined single-query predicate");
    }

    /// <summary>
    /// Verifies that the combined single-query predicate correctly covers the TOCTOU window
    /// described in the issue. In the original two-query implementation, a WorkItem that
    /// transitioned from an active status to terminal between the two queries would cause both
    /// to return false and the endpoint to report isDistributed = false. Status and CompletedAt
    /// are written atomically in a single transaction via WorkItemMutationFactory, so the gap
    /// can only occur between the two separate DB round-trips — which the single-query fix
    /// eliminates. This test confirms that a recently-transitioned terminal item (CompletedAt
    /// within cooldown) is correctly reported as distributed, the same result the old first
    /// query would have given if it had run while the item was still active.
    ///
    /// NOTE: "terminal with CompletedAt=null" is not a valid state in production — Status and
    /// CompletedAt are written atomically. The scenarios covered here (active + null CompletedAt,
    /// and terminal + CompletedAt within cooldown) are the correct representations of the
    /// in-flight and just-completed states respectively.
    /// </summary>
    [Fact]
    public async Task GetIsDistributed_ReturnsTrue_WhenTerminalWithCompletedAtJustWritten()
    {
        // Seed a Failed item with CompletedAt = just now — simulates the state the row is in
        // immediately after the atomic Status+CompletedAt write. The single combined query
        // must catch this via the recently-terminal branch, which is the same result the old
        // two-query implementation would have given had both queries seen consistent data.
        var issueId = $"toctou-just-written-{Guid.NewGuid():N}";
        SeedEntity(WorkItemStatus.Failed, issueIdentifier: issueId,
            completedAt: DateTimeOffset.UtcNow);

        var response = await _client.GetAsync(
            $"/api/work-items/is-distributed?issueIdentifier={issueId}&issueProviderConfigId=prov-seed");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(PipelineJsonOptions.Default);
        body.GetProperty("isDistributed").GetBoolean().Should().BeTrue(
            "a WorkItem that just completed (terminal status, CompletedAt within cooldown) " +
            "must be considered distributed by the combined single-query predicate; " +
            "this is the state the row is in immediately after the atomic Status+CompletedAt write");
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

    // TODO: Add a test for GetIsDistributed returning false when a terminal item's CompletedAt
    // is outside the dedup cooldown window (CompletedAt < UtcNow - DefaultRestartDedupCooldown).
    // The current tests only cover: (a) no matching item, (b) recently-terminal within window,
    // (c) active status. The expired-terminal boundary condition is untested — a regression that
    // widens the cooldown bound or drops the `CompletedAt >= since` clause would not be caught.

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

    // ── PriorityWeight — AC tests for issue #2172 ────────────────────────────

    /// <summary>
    /// AC: POST /api/work-items with InitiatedBy="manual" creates entity with PriorityWeight=100.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_WithManualInitiatedBy_SetsPriorityWeightTo100()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"issue-manual-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "manual",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        };

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        using var db = _factory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync(id);
        entity!.PriorityWeight.Should().Be(100, "manual dispatch must receive PriorityWeight=100");
    }

    /// <summary>
    /// AC: POST /api/work-items with InitiatedBy="loop" creates entity with PriorityWeight=0.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_WithLoopInitiatedBy_SetsPriorityWeightToZero()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"issue-loop-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 3600
        };

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        using var db = _factory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync(id);
        entity!.PriorityWeight.Should().Be(0, "loop dispatch must receive PriorityWeight=0");
    }

    // TODO: Add tests for InitiatedBy values that are neither "manual" nor "loop" (e.g. null,
    // empty string, "automated", "cron", "MANUAL"). The production code uses StringComparison.Ordinal
    // and maps everything non-"manual" to PriorityWeight=0. A caller sending "MANUAL" (wrong case)
    // would silently receive weight 0. At minimum test null or empty → PriorityWeight=0 to lock in
    // the intended fallback behavior.

    // ── TimeoutSeconds clamping (issue #2745) ─────────────────────────────────

    /// <summary>
    /// AC (issue #2745): POST /api/work-items with TimeoutSeconds = 0 must store
    /// <c>PipelineConstants.DefaultAgentTimeout</c> (1800s) rather than zero.
    /// A zero stored value causes ReconciliationLoop to immediately force-fail Running items.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_WithZeroTimeoutSeconds_StoresDefaultTimeoutSeconds()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"issue-timeout-zero-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 0
        };

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        // TODO [WARNING]: synchronous `using` wrapping an async operation — PipelineDbContext implements
        // IAsyncDisposable and should be disposed asynchronously. Change to `await using var db = _factory.CreateDbContext();`
        // to avoid a potential ObjectDisposedException if the context is disposed while FindAsync is in-flight.
        // (DotNetSpecialist review [WARNING])
        using var db = _factory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync(id);
        entity!.TimeoutSeconds.Should().Be(
            (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            "a zero TimeoutSeconds must be clamped to DefaultAgentTimeout (1800s) at the insert path");
    }

    /// <summary>
    /// AC (issue #2745): POST /api/work-items with a negative TimeoutSeconds must store
    /// <c>PipelineConstants.DefaultAgentTimeout</c> (1800s) rather than the negative value.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_WithNegativeTimeoutSeconds_StoresDefaultTimeoutSeconds()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"issue-timeout-neg-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = -1
        };

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        // TODO [WARNING]: synchronous `using` wrapping an async operation — IAsyncDisposable mismatch,
        // same as the sibling test above. Change to `await using var db = _factory.CreateDbContext();`.
        // (DotNetSpecialist review [WARNING])
        using var db = _factory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync(id);
        entity!.TimeoutSeconds.Should().Be(
            (int)PipelineConstants.DefaultAgentTimeout.TotalSeconds,
            "a negative TimeoutSeconds must be clamped to DefaultAgentTimeout (1800s) at the insert path");
    }

    /// <summary>
    /// Regression guard (issue #2745): POST /api/work-items with a positive TimeoutSeconds must
    /// store exactly the provided value — the clamp must not alter positive values.
    /// </summary>
    [Fact]
    public async Task CreateWorkItem_WithPositiveTimeoutSeconds_StoresAsProvided()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier($"issue-timeout-pos-{Guid.NewGuid():N}"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 600
        };

        var response = await _client.PostAsJsonAsync("/api/work-items", request, PipelineJsonOptions.Default);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var id = await response.Content.ReadFromJsonAsync<Guid>(PipelineJsonOptions.Default);

        // TODO [WARNING]: synchronous `using` wrapping an async operation — IAsyncDisposable mismatch,
        // same as the sibling tests above. Change to `await using var db = _factory.CreateDbContext();`.
        // (DotNetSpecialist review [WARNING])
        using var db = _factory.CreateDbContext();
        var entity = await db.WorkItems.FindAsync(id);
        entity!.TimeoutSeconds.Should().Be(600,
            "a positive TimeoutSeconds must be stored exactly as provided — clamping must not alter it");
    }

    /// <summary>
    /// AC: GET /api/work-items/pending returns high-weight item before low-weight item
    /// regardless of CreatedAt.
    /// </summary>
    [Fact]
    public async Task GetPendingWorkItems_ReturnsHighWeightBeforeLowWeight_RegardlessOfCreatedAt()
    {
        var issuePrefix = $"prio-order-{Guid.NewGuid():N}";

        // Seed a low-weight item with an earlier CreatedAt (was created "first")
        var lowWeightEntity = SeedEntity(
            WorkItemStatus.Pending,
            issueIdentifier: $"{issuePrefix}-low",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        lowWeightEntity = UpdateEntityPriorityWeight(lowWeightEntity.Id, 0);

        // Seed a high-weight item with a later CreatedAt (created "second")
        var highWeightEntity = SeedEntity(
            WorkItemStatus.Pending,
            issueIdentifier: $"{issuePrefix}-high",
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        highWeightEntity = UpdateEntityPriorityWeight(highWeightEntity.Id, 100);

        var pendingResponse = await _client.GetAsync("/api/work-items/pending?maxResults=100");
        pendingResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await pendingResponse.Content
            .ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        // Verify both entities carry the expected PriorityWeight values in the response
        // (confirms that UpdateEntityPriorityWeight persisted before the HTTP call was made)
        var lowItem = items!.FirstOrDefault(i => i.Id == lowWeightEntity.Id);
        var highItem = items!.FirstOrDefault(i => i.Id == highWeightEntity.Id);

        lowItem.Should().NotBeNull("low-weight item must be in pending list");
        highItem.Should().NotBeNull("high-weight item must be in pending list");
        lowItem!.PriorityWeight.Should().Be(0, "low-weight item must have PriorityWeight=0 as seeded");
        highItem!.PriorityWeight.Should().Be(100, "high-weight item must have PriorityWeight=100 as seeded");

        // Filter to just the two fixture items and assert the ordering invariant directly,
        // so interleaved items from other parallel tests cannot produce false positives.
        var fixtureItems = items
            .Where(i => i.Id == lowWeightEntity.Id || i.Id == highWeightEntity.Id)
            .ToList();

        fixtureItems.Should().HaveCount(2);
        fixtureItems[0].Id.Should().Be(highWeightEntity.Id,
            "high-weight item must appear before low-weight item regardless of CreatedAt");
        fixtureItems[1].Id.Should().Be(lowWeightEntity.Id,
            "low-weight item must appear after high-weight item");
    }

    /// <summary>
    /// AC: PriorityWeight field is present in GET /api/work-items/pending response.
    /// </summary>
    [Fact]
    public async Task GetPendingWorkItems_IncludesPriorityWeightField()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);
        UpdateEntityPriorityWeight(entity.Id, 42);

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=100");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content
            .ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        var found = items!.FirstOrDefault(i => i.Id == entity.Id);
        found.Should().NotBeNull();
        found!.PriorityWeight.Should().Be(42);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority with valid weight returns 200 and persists value.
    /// </summary>
    [Fact]
    public async Task PostPriority_WithValidWeight_Returns200AndPersistsValue()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = 500 },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.PriorityWeight.Should().Be(500);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority with weight &lt; 0 returns 400.
    /// </summary>
    [Fact]
    public async Task PostPriority_WithNegativeWeight_Returns400()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = -1 },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority with weight &gt; 1000 returns 400.
    /// </summary>
    [Fact]
    public async Task PostPriority_WithWeightAbove1000_Returns400()
    {
        var entity = SeedEntity(WorkItemStatus.Pending);

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = 1001 },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority on a non-Pending item returns 409.
    /// </summary>
    [Theory]
    [InlineData(WorkItemStatus.Dispatched)]
    [InlineData(WorkItemStatus.Running)]
    [InlineData(WorkItemStatus.Succeeded)]
    [InlineData(WorkItemStatus.Failed)]
    [InlineData(WorkItemStatus.Cancelled)]
    public async Task PostPriority_OnNonPendingItem_Returns409(WorkItemStatus nonPendingStatus)
    {
        var entity = SeedEntity(nonPendingStatus);

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = 100 },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority on non-existent item returns 404.
    /// </summary>
    [Fact]
    public async Task PostPriority_OnNonExistentItem_Returns404()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{Guid.NewGuid()}/priority",
            new { priorityWeight = 100 },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // TODO: Add a test for POST /{id}/priority with a missing or empty body (i.e. priorityWeight
    // is null after deserialization, sending `{}` or no body). The production handler returns
    // 400 "priorityWeight is required." for PriorityWeightRequest.PriorityWeight == null,
    // but this path is currently untested.

    /// <summary>
    /// AC: POST /api/work-items/{id}/priority with boundary values 0 and 1000 returns 200.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    public async Task PostPriority_WithBoundaryWeights_Returns200(int weight)
    {
        var entity = SeedEntity(WorkItemStatus.Pending);

        var response = await _client.PostAsJsonAsync(
            $"/api/work-items/{entity.Id}/priority",
            new { priorityWeight = weight },
            PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.PriorityWeight.Should().Be(weight);
    }

    /// <summary>
    /// AC: POST /api/work-items/{id}/requeue on a Failed item with PriorityWeight=100
    /// preserves PriorityWeight after the transition back to Pending.
    /// </summary>
    [Fact]
    public async Task RequeueWorkItem_PreservesPriorityWeight_AfterTransitionFromFailed()
    {
        // Seed a Failed entity with PriorityWeight=100
        var entity = SeedEntity(WorkItemStatus.Failed, failureReason: FailureReason.AgentError);
        UpdateEntityPriorityWeight(entity.Id, 100);

        // Verify the weight was persisted before calling requeue, so a silent failure in
        // UpdateEntityPriorityWeight cannot mask a bug in the requeue transition.
        using (var dbBefore = _factory.CreateDbContext())
        {
            var before = await dbBefore.WorkItems.FindAsync(entity.Id);
            before!.PriorityWeight.Should().Be(100, "PriorityWeight must be 100 before requeue is called");
        }

        // Requeue Failed → Pending
        var response = await _client.PostAsync($"/api/work-items/{entity.Id}/requeue", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // PriorityWeight must be preserved (requeue does not touch the column)
        using var db = _factory.CreateDbContext();
        var updated = await db.WorkItems.FindAsync(entity.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.PriorityWeight.Should().Be(100, "PriorityWeight must be preserved across requeue transitions");
    }

    // ── Priority test helpers ─────────────────────────────────────────────────

    private WorkItemEntity UpdateEntityPriorityWeight(Guid id, int priorityWeight)
    {
        using var db = _factory.CreateDbContext();
        var entity = db.WorkItems.Find(id)!;
        entity.PriorityWeight = priorityWeight;
        db.SaveChanges();
        return entity;
    }

    // ── GET /pending — consolidation always included ──────────────────────────

    [Fact]
    public async Task GetPendingWorkItems_IncludesConsolidationItems()
    {
        // Consolidation WorkItems are now enqueued as Pending (unified dispatch path, #2566).
        // GET /api/work-items/pending must include them so the WorkItemDispatchPoller can dispatch them.
        // TODO: [WARNING] This test only verifies the "consolidation items are included" half. There is
        // no assertion that a non-Consolidation Pending item is also returned, which would confirm that
        // removing the old `TaskType != Consolidation` filter didn't accidentally break the general Pending
        // query. Seed an Implementation Pending item alongside the Consolidation one and assert both appear.
        var consolidation = SeedEntity(WorkItemStatus.Pending, taskType: WorkItemTaskType.Consolidation);

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().Contain(i => i.Id == consolidation.Id,
            "consolidation Pending items must be returned by GET /api/work-items/pending so the WorkItemDispatchPoller can dispatch them");
    }

    [Fact]
    public async Task GetPendingWorkItems_ExcludesNonPendingConsolidationItems()
    {
        // Only Pending items should be returned — non-Pending consolidation items must be excluded.
        var running = SeedEntity(WorkItemStatus.Running, taskType: WorkItemTaskType.Consolidation);

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Should().NotContain(i => i.Id == running.Id,
            "non-Pending consolidation items must not appear in the pending queue");
    }

    /// <summary>
    /// AC: GET /api/work-items/pending applies RunType tier ordering as the primary sort:
    /// Review &gt; Decomposition &gt; Implementation &gt; Consolidation, regardless of CreatedAt.
    /// Within the same tier, higher PriorityWeight comes first (secondary sort).
    /// Decision: decisions.md "Dispatch priority: static ordering Review > Decomposition > Implementation > Consolidation"
    /// and "PriorityWeight: secondary sort key within RunType tier".
    /// </summary>
    [Fact]
    public async Task GetPendingWorkItems_OrderedByRunTypeTierThenPriorityWeightThenCreatedAt()
    {
        var prefix = $"tier-order-{Guid.NewGuid():N}";
        var base_ = DateTimeOffset.UtcNow.AddMinutes(-30);

        // Seed one item per tier — all at PriorityWeight=0, Consolidation created first (oldest).
        // Expected order after tier sort: Review, Decomposition, Implementation, Consolidation.
        var consolidation = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-consolidation",
            taskType: WorkItemTaskType.Consolidation,
            createdAt: base_);
        var implementation = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-implementation",
            taskType: WorkItemTaskType.Implementation,
            createdAt: base_.AddMinutes(1));
        var decomposition = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-decomposition",
            taskType: WorkItemTaskType.Decomposition,
            createdAt: base_.AddMinutes(2));
        var review = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-review",
            taskType: WorkItemTaskType.Review,
            createdAt: base_.AddMinutes(3));

        // Seed a second Implementation at higher PriorityWeight to verify within-tier secondary sort.
        var highWeightImpl = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-implementation-high",
            taskType: WorkItemTaskType.Implementation,
            createdAt: base_.AddMinutes(4));
        highWeightImpl = UpdateEntityPriorityWeight(highWeightImpl.Id, 100);

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        // Filter to just the fixture items in original list order.
        var fixtureIds = new HashSet<Guid> { review.Id, decomposition.Id, implementation.Id, consolidation.Id, highWeightImpl.Id };
        var fixture = items!.Where(i => fixtureIds.Contains(i.Id)).ToList();
        fixture.Should().HaveCount(5, "all 5 seeded items must be in the pending list");

        var reviewIdx = fixture.FindIndex(i => i.Id == review.Id);
        var decompIdx = fixture.FindIndex(i => i.Id == decomposition.Id);
        var highWeightIdx = fixture.FindIndex(i => i.Id == highWeightImpl.Id);
        var implIdx = fixture.FindIndex(i => i.Id == implementation.Id);
        var consolidIdx = fixture.FindIndex(i => i.Id == consolidation.Id);

        // Tier ordering: Review < Decomposition < Implementation(both) < Consolidation
        reviewIdx.Should().BeLessThan(decompIdx, "Review must come before Decomposition");
        decompIdx.Should().BeLessThan(highWeightIdx, "Decomposition must come before Implementation");
        decompIdx.Should().BeLessThan(implIdx, "Decomposition must come before Implementation");
        // Within Implementation tier: higher PriorityWeight dispatches first
        highWeightIdx.Should().BeLessThan(implIdx,
            "high-weight Implementation (100) must precede low-weight Implementation (0) within the tier");
        implIdx.Should().BeLessThan(consolidIdx, "Implementation must come before Consolidation");
    }

    /// <summary>
    /// Starvation boundary: when 51 Review items are pending and maxResults=50, the Take(50)
    /// window fills entirely with Reviews and the single Implementation item is absent.
    /// This documents the intentional starvation contract: lower-tier items are invisible to
    /// the WorkItemDispatchPoller for that cycle whenever a higher tier has 50+ pending items.
    /// Decision: decisions.md "Dispatch priority: static ordering Review > Decomposition > Implementation > Consolidation".
    /// </summary>
    [Fact]
    public async Task GetPendingWorkItems_HighTierBacklogFillsWindow_LowerTierItemAbsent()
    {
        var prefix = $"starvation-{Guid.NewGuid():N}";
        var base_ = DateTimeOffset.UtcNow.AddMinutes(-60);

        // Seed 51 Review items (one more than the default maxResults=50 window).
        var reviewIds = new List<Guid>();
        for (var i = 0; i < 51; i++)
        {
            var r = SeedEntity(WorkItemStatus.Pending,
                issueIdentifier: $"{prefix}-review-{i}",
                taskType: WorkItemTaskType.Review,
                createdAt: base_.AddSeconds(i));
            reviewIds.Add(r.Id);
        }

        // Seed one Implementation item — older than all Reviews, PriorityWeight=0.
        // Without tier ordering it would rank near the top (older CreatedAt).
        // With tier ordering it is pushed out of the 50-slot window.
        var impl = SeedEntity(WorkItemStatus.Pending,
            issueIdentifier: $"{prefix}-implementation",
            taskType: WorkItemTaskType.Implementation,
            createdAt: base_.AddMinutes(-10));

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=50");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();
        items!.Count.Should().BeLessThanOrEqualTo(50);

        // The Implementation item must NOT appear — the 50-slot window is fully consumed by Reviews.
        items.Should().NotContain(i => i.Id == impl.Id,
            "the Implementation item must be absent from the maxResults=50 window when 51 Review items are pending");

        // All returned items must be Review tier (within the fixture, at least).
        // Verify the 50 Review fixture items that made it into the window are Review type.
        var fixtureReviews = items.Where(i => reviewIds.Contains(i.Id)).ToList();
        fixtureReviews.Should().OnlyContain(i => i.TaskType == WorkItemTaskType.Review,
            "items appearing in the window from our fixture must all be Review tier");
    }

    // ── Payload deserialization characterization (Issue #2776) ────────────────────
    // These tests document and guard the behavior of the centralized WorkItemPayload.TryDeserialize
    // helper, verifying that PipelineJsonOptions.Lenient (PropertyNameCaseInsensitive) is used
    // consistently across all four deserialization call sites.

    [Fact]
    public async Task GetPendingWorkItems_WithPascalCasePayload_ExtractsDisplayFields()
    {
        // Arrange: seed a Pending work item whose Payload is PascalCase-serialized.
        // This simulates a legacy payload written before camelCase was enforced.
        // With PipelineJsonOptions.Default (case-sensitive) the payload would produce null
        // display fields. With PipelineJsonOptions.Lenient it must be parsed correctly.
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"pascal-pending-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-pascal",
                Status = WorkItemStatus.Pending,
                // Hand-crafted PascalCase JSON — mimics a payload written by an older serializer.
                Payload = """
                    {
                        "IssueIdentifier": "owner/repo#1",
                        "IssueProviderConfigId": "prov-pascal",
                        "RepoProviderConfigId": "repo-pascal",
                        "InitiatedBy": "legacy-loop",
                        "TaskType": "Implementation",
                        "AgentSelector": "dotnet",
                        "TimeoutSeconds": 3600,
                        "IssueDetail": {
                            "Identifier": "owner/repo#1",
                            "Title": "PascalCase issue title",
                            "Description": "",
                            "Labels": []
                        }
                    }
                    """,
                AgentSelector = "dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the seeded PascalCase work item must appear in /pending");
        dto!.IssueTitle.Should().Be("PascalCase issue title",
            "IssueTitle must be extracted from PascalCase payload via Lenient deserialization");
        dto.InitiatedBy.Should().Be("legacy-loop",
            "InitiatedBy must be extracted from PascalCase payload via Lenient deserialization");
    }

    [Fact]
    public async Task GetPendingWorkItems_WithMalformedPayload_ReturnsNullDisplayFields()
    {
        // Arrange: seed a Pending work item with a corrupted Payload.
        // The endpoint must return 200 with null display fields — not a 500.
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"malformed-pending-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-corrupt",
                Status = WorkItemStatus.Pending,
                Payload = "{not-valid-json",
                AgentSelector = "dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SaveChanges();
        }

        var response = await _client.GetAsync("/api/work-items/pending?maxResults=500");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a malformed payload must not cause a 500 — the row must appear with null display fields");
        var items = await response.Content.ReadFromJsonAsync<List<PendingWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the malformed-payload work item must still appear in /pending");
        dto!.IssueTitle.Should().BeNull("IssueTitle must be null when payload is malformed");
        dto.InitiatedBy.Should().BeNull("InitiatedBy must be null when payload is malformed");
    }

    [Fact]
    public async Task GetActiveWorkItems_WithPascalCasePayload_ExtractsDisplayFields()
    {
        // Arrange: seed an active (Running) work item with a PascalCase payload.
        // Closes the pre-existing TODO comment about missing PascalCase coverage for GetActiveWorkItems.
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"pascal-active-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-pascal",
                Status = WorkItemStatus.Running,
                Payload = """
                    {
                        "IssueIdentifier": "owner/repo#2",
                        "IssueProviderConfigId": "prov-pascal",
                        "RepoProviderConfigId": "repo-pascal",
                        "InitiatedBy": "legacy-loop-active",
                        "TaskType": "Implementation",
                        "AgentSelector": "dotnet",
                        "TimeoutSeconds": 3600,
                        "IssueDetail": {
                            "Identifier": "owner/repo#2",
                            "Title": "PascalCase active title",
                            "Description": "",
                            "Labels": []
                        }
                    }
                    """,
                AgentSelector = "dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-300),
                DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-300)
            });
            db.SaveChanges();
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the PascalCase active work item must appear in /active");
        dto!.IssueTitle.Should().Be("PascalCase active title",
            "IssueTitle must be extracted from PascalCase payload via Lenient deserialization");
        dto.InitiatedBy.Should().Be("legacy-loop-active",
            "InitiatedBy must be extracted from PascalCase payload via Lenient deserialization");
    }

    [Fact]
    public async Task GetActiveWorkItems_WithMalformedPayload_ReturnsNullDisplayFields()
    {
        // Arrange: seed a Running work item with a corrupted Payload.
        // Closes the pre-existing TODO [WARNING] comment in this file about the JsonException
        // branch in GetActiveWorkItems being untested.
        var workItemId = Guid.NewGuid();
        using (var db = _factory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"malformed-active-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-corrupt",
                Status = WorkItemStatus.Running,
                Payload = "{not-valid-json",
                AgentSelector = "dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-300),
                DispatchedAt = DateTimeOffset.UtcNow.AddSeconds(-300)
            });
            db.SaveChanges();
        }

        var response = await _client.GetAsync("/api/work-items/active?olderThanSeconds=0");
        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "a malformed payload must not cause a 500 — the row must appear with null display fields");
        var items = await response.Content.ReadFromJsonAsync<List<ActiveWorkItemDto>>(PipelineJsonOptions.Default);
        items.Should().NotBeNull();

        var dto = items!.FirstOrDefault(i => i.Id == workItemId);
        dto.Should().NotBeNull("the malformed-payload active work item must still appear in /active");
        dto!.IssueTitle.Should().BeNull("IssueTitle must be null when payload is malformed");
        dto.InitiatedBy.Should().BeNull("InitiatedBy must be null when payload is malformed");
    }
}
