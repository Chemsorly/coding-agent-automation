using CodingAgentWebUI.E2ETests.Infrastructure;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;

namespace CodingAgentWebUI.E2ETests.Tests;

/// <summary>
/// Kubernetes-mode E2E tests: validates the K8s-specific dispatch pipeline where
/// WorkItems are inserted as Pending, DispatchService polls and creates K8s Jobs,
/// and KubernetesWorkDistributor handles distribution.
/// Uses FakeKubernetesJobClient to capture Job creation calls without real K8s.
/// </summary>
[Trait("Category", "E2E")]
[Trait("Feature", "K8sMode")]
[Collection(E2ECollection.Name)]
public sealed class K8sModeTests : HeadlessE2ETestBase
{
    public K8sModeTests(E2EFixture fixture) : base(fixture) { }

    // ═══════════════════════════════════════════════════════════════════════
    // G5: KubernetesWorkDistributor — DistributeAsync inserts WorkItem row
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_DistributeAsync_InsertsWorkItemAsPending()
    {
        // Act: distribute via the real KubernetesWorkDistributor
        var result = await DistributeDirectlyAsync("k8s-issue-100", "kiro,dotnet");

        // Assert: distribution succeeded
        Assert.True(result.Success, $"Distribution failed: {result.ErrorMessage}");
        Assert.NotNull(result.WorkItemId);
        Assert.True(result.Queued, "K8s mode always queues (Pending) — DispatchService handles pod creation");

        // Assert: WorkItem exists in DB as Pending
        var workItemId = Guid.Parse(result.WorkItemId);
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);

        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Pending, item.Status);
        Assert.Equal("k8s-issue-100", item.IssueIdentifier);
        Assert.Equal("kiro,dotnet", item.AgentSelector);
        Assert.Null(item.DispatchedAt); // Not dispatched yet — DispatchService does this
    }

    [Fact]
    public async Task K8sMode_DistributeAsync_DuplicateIssue_SecondRejected()
    {
        // First distribution
        var r1 = await DistributeDirectlyAsync("k8s-issue-101");
        Assert.True(r1.Success);

        // Second distribution for same issue — should detect existing active WorkItem
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var isDuplicate = await distributor.IsIssueDistributedAsync("k8s-issue-101", "issue-e2e", CancellationToken.None);

        Assert.True(isDuplicate, "Issue should be detected as already distributed");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G1: KubernetesWorkDistributor inserts Pending + K8s Job creation path
    // (DispatchService not running in this test factory — tested via unit tests.
    //  Here we verify the DB insert + transition path works correctly.)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_WorkItemTransition_PendingToDispatched()
    {
        // Arrange: insert a Pending WorkItem
        var workItemId = await InsertPendingWorkItemAsync("k8s-dispatch-200", "kiro,dotnet");

        // Act: manually transition to Dispatched (simulating what DispatchService does)
        var transitionService = Fixture.Factory.Services.GetRequiredService<CodingAgentWebUI.Infrastructure.Persistence.Services.WorkItemTransitionService>();
        var transitioned = await transitionService.TransitionAsync(
            workItemId, WorkItemStatus.Dispatched,
            w =>
            {
                w.DispatchedAt = DateTimeOffset.UtcNow;
                w.K8sJobName = $"caa-{workItemId:N}"[..Math.Min(40, $"caa-{workItemId:N}".Length)];
            }, ct: CancellationToken.None);

        Assert.True(transitioned);

        // Verify final state
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Dispatched, item.Status);
        Assert.NotNull(item.DispatchedAt);
        Assert.NotNull(item.K8sJobName);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G2: ReconciliationService — K8s Job completes → WorkItem Succeeded
    // (tested via direct invocation since Watch is disabled)
    // ═══════════════════════════════════════════════════════════════════════

    // ═══════════════════════════════════════════════════════════════════════
    // G4: KubernetesWorkDistributor is the active distributor
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_DistributorIsKubernetesType()
    {
        // Verify the factory correctly wired KubernetesWorkDistributor
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        Assert.IsType<KubernetesWorkDistributor>(distributor);

        // Verify it inserts as Pending (not Dispatched like SignalR mode)
        var result = await DistributeDirectlyAsync("k8s-type-check-500");
        Assert.True(result.Success);
        Assert.True(result.Queued); // K8s mode always returns Queued=true

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.IssueIdentifier == "k8s-type-check-500");
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Pending, item.Status);
        Assert.Null(item.DispatchedAt); // Not dispatched — DispatchService does this
    }

    // ═══════════════════════════════════════════════════════════════════════
    // Additional: K8s mode WorkItem cancellation
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_CancelWorkItem_TransitionsToCancelled()
    {
        // Arrange: distribute a work item
        var result = await DistributeDirectlyAsync("k8s-cancel-600");
        Assert.True(result.Success);
        var workItemId = result.WorkItemId!;

        // Act: cancel via IWorkDistributor
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var cancelled = await distributor.CancelJobAsync(workItemId, CancellationToken.None);

        Assert.True(cancelled);

        // Assert: WorkItem is Cancelled
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == Guid.Parse(workItemId));
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Cancelled, item.Status);
        Assert.NotNull(item.CompletedAt);
    }

    [Fact]
    public async Task K8sMode_GetJobStatus_ReturnsMappedStatus()
    {
        // Arrange: distribute
        var result = await DistributeDirectlyAsync("k8s-status-700");
        Assert.True(result.Success);

        // Act: query status
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var status = await distributor.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);

        // Assert: should be Pending (K8s mode inserts as Pending)
        Assert.Equal(JobDistributionStatus.Pending, status);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G7: Agent HTTP Assignment Fetch — GET /api/work-items/{id}/assignment
    // Validates: fix 0522f64a (missing JSON serializer) + payload deserialization
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentFetchesAssignment_ReturnsValidPayload()
    {
        // Arrange: insert a WorkItem with a full Payload (as KubernetesWorkDistributor does)
        var result = await DistributeDirectlyAsync("k8s-fetch-assign-900");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        // Transition to Dispatched (as DispatchService would do)
        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w => w.DispatchedAt = DateTimeOffset.UtcNow, ct: CancellationToken.None);

        // Act: call the assignment endpoint (same as WorkItemHttpClient.GetAssignmentAsync)
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var response = await httpClient.GetAsync($"/api/work-items/{workItemId}/assignment");

        // Assert: endpoint returns 200 with valid assignment JSON
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var json = await response.Content.ReadAsStringAsync();
        Assert.NotEmpty(json);

        // Verify key fields are present in the response
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal(workItemId.ToString(), root.GetProperty("jobId").GetString());
        Assert.Equal("k8s-fetch-assign-900", root.GetProperty("issueIdentifier").GetString());
        Assert.Equal("repo-e2e", root.GetProperty("repoProviderConfigId").GetString());
        Assert.Equal("e2e-test", root.GetProperty("initiatedBy").GetString());
    }

    [Fact]
    public async Task K8sMode_AgentFetchesAssignment_TerminalStatus_Returns410()
    {
        // Arrange: insert a WorkItem that's already Succeeded (terminal)
        var workItemId = Guid.NewGuid();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "k8s-fetch-terminal-901",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Succeeded, // Already terminal
            Payload = "{}",
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();

        // Act
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var response = await httpClient.GetAsync($"/api/work-items/{workItemId}/assignment");

        // Assert: 410 Gone (agent should exit gracefully)
        Assert.Equal(System.Net.HttpStatusCode.Gone, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G8: Agent Status Transitions — POST /api/work-items/{id}/status
    // Validates: fix 15117f77 (Running rejected → abort)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentPostsRunningStatus_TransitionAccepted()
    {
        // Arrange: distribute + transition to Dispatched
        var result = await DistributeDirectlyAsync("k8s-status-running-1000");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w => w.DispatchedAt = DateTimeOffset.UtcNow, ct: CancellationToken.None);

        // Act: agent POSTs Running status (as WorkItemAgentService does)
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new { Status = "Running", AgentId = "caa-test-pod" };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status", statusBody);

        // Assert: transition accepted
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // Verify DB state
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Running, item.Status);
        Assert.Equal("caa-test-pod", item.AssignedAgentId);
    }

    [Fact]
    public async Task K8sMode_AgentPostsRunningStatus_InvalidTransition_Returns400()
    {
        // Arrange: WorkItem is Pending (can't go directly to Running — must go through Dispatched)
        var result = await DistributeDirectlyAsync("k8s-status-invalid-1001");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);
        // WorkItem is Pending — Running is not a valid transition from Pending

        // Act
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new { Status = "Running", AgentId = "caa-test-pod-2" };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status", statusBody);

        // Assert: rejected (400 Bad Request — invalid state transition)
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);

        // Assert: DB state unchanged — still Pending, no agent assigned
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Pending, item.Status);
        Assert.Null(item.AssignedAgentId);
    }

    [Fact]
    public async Task K8sMode_AgentPostsFailedStatus_TransitionAccepted()
    {
        // Arrange: distribute → dispatch → running
        var result = await DistributeDirectlyAsync("k8s-status-failed-1002");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w => w.DispatchedAt = DateTimeOffset.UtcNow, ct: CancellationToken.None);
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Running, ct: CancellationToken.None);

        // Act: agent POSTs Failed status (as WorkItemAgentService does on pipeline failure)
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new
        {
            Status = "Failed",
            AgentId = "caa-test-pod-3",
            ErrorMessage = "Pipeline execution failed: token refresh error"
        };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status", statusBody);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Failed, item.Status);
        Assert.NotNull(item.CompletedAt);
        Assert.Contains("token refresh", item.ErrorMessage ?? "");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G9: Agent Hub Registration + Token Refresh (full SignalR lifecycle)
    // Validates: fix 4dd8127d (missing RegisterAgent) + fix 59c5a258 (token vending)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentRegistersWithActiveJob_TokenRefreshSucceeds()
    {
        // Arrange: seed a repo provider config with a static AccessToken
        // (avoids needing real GitHub App JWT — uses the GitLab PAT path in RequestTokenRefresh)
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-k8s-token-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "K8s Token Test Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-test-token-12345"
            }
        }, CancellationToken.None);

        // Insert a WorkItem with repoProviderConfigId pointing to our test provider
        var workItemId = Guid.NewGuid();
        var payloadRequest = new JobDistributionRequest
        {
            IssueIdentifier = "k8s-token-refresh-1100",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-k8s-token-test",
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            ProjectId = WellKnownIds.DefaultProjectId,
            InitiatedBy = "k8s-e2e-test"
        };
        var payloadJson = System.Text.Json.JsonSerializer.Serialize(payloadRequest, PipelineJsonOptions.Default);

        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "k8s-token-refresh-1100",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                Payload = payloadJson,
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        // Act: connect as a K8s agent with ActiveJobState (mimicking WorkItemAgentService)
        await using var agent = new FakeAgentClient("caa-k8s-token-agent", "kiro", "dotnet");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            workItemId.ToString(),
            "k8s-token-refresh-1100",
            "repo-k8s-token-test");

        // Assert: agent is registered and busy
        var registry = Fixture.AgentRegistry;
        var entry = registry.GetByAgentId("caa-k8s-token-agent");
        Assert.NotNull(entry);
        Assert.Equal(AgentStatus.Busy, entry.Status);
        Assert.Equal(workItemId.ToString(), entry.ActiveJobId);

        // Act: call RequestTokenRefresh (the operation that was failing in production)
        var tokenResponse = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);

        // Assert: token is returned (the static AccessToken path)
        Assert.NotNull(tokenResponse);
        Assert.Equal("fake-test-token-12345", tokenResponse.Token);
    }

    [Fact]
    public async Task K8sMode_AgentWithoutRegistration_TokenRefreshFails()
    {
        // Arrange: connect but do NOT register (old buggy behavior)
        // This test documents the failure mode that was happening in production
        await using var agent = new FakeAgentClient("caa-k8s-noreg-agent", "kiro");
        await agent.ConnectAsync(Fixture.AgentHubUrl, E2EWebApplicationFactory.TestApiKey);

        // Act & Assert: RequestTokenRefresh should fail because agent has no ActiveJobId
        // The [RequiresActiveJob] filter rejects because ActiveJobId (null) != jobId
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await agent.RequestTokenRefreshAsync("nonexistent-job-id", ProviderKind.Repository);
        });

        // The exception should be a HubException from the [RequiresActiveJob] filter
        // (either "not assigned" or "No active run" — both indicate the call was rejected)
        Assert.True(
            exception.Message.Contains("not assigned", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("No active run", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("nonexistent-job-id", StringComparison.OrdinalIgnoreCase),
            $"Expected rejection for unauthorized job access, got: {exception.Message}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // ═══════════════════════════════════════════════════════════════════════
    // G11: Edge Cases — Agent status POST scenarios not yet seen in production
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentPostsSucceededStatus_WithResultPayload_Accepted()
    {
        // Arrange: full lifecycle → Dispatched → Running
        var result = await DistributeDirectlyAsync("k8s-status-succeeded-1300");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w => w.DispatchedAt = DateTimeOffset.UtcNow, ct: CancellationToken.None);
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Running, ct: CancellationToken.None);

        // Act: agent POSTs Succeeded with a result payload (completion data)
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new
        {
            Status = "Succeeded",
            AgentId = "caa-success-pod",
            Result = System.Text.Json.JsonSerializer.Serialize(new
            {
                FinalStep = "Completed",
                PullRequestUrl = "https://github.com/org/repo/pull/42",
                FilesChangedCount = 5,
                LinesAdded = 120,
                LinesRemoved = 30
            })
        };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status", statusBody);

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal(WorkItemStatus.Succeeded, item.Status);
        Assert.NotNull(item.CompletedAt);
        Assert.NotNull(item.Result);
        Assert.Contains("pull/42", item.Result);
    }

    [Fact]
    public async Task K8sMode_AgentPostsDuplicateTerminalStatus_IsIdempotent()
    {
        // Arrange: WorkItem already in terminal state (Failed)
        var result = await DistributeDirectlyAsync("k8s-status-duplicate-1301");
        Assert.True(result.Success);
        var workItemId = Guid.Parse(result.WorkItemId!);

        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w => w.DispatchedAt = DateTimeOffset.UtcNow, ct: CancellationToken.None);
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Running, ct: CancellationToken.None);
        await transitionService.TransitionAsync(workItemId, WorkItemStatus.Failed,
            w => { w.CompletedAt = DateTimeOffset.UtcNow; w.ErrorMessage = "First failure"; },
            ct: CancellationToken.None);

        // Act: agent crashes and restarts, tries to POST Failed again
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new { Status = "Failed", AgentId = "caa-dup-pod", ErrorMessage = "Second failure attempt" };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status", statusBody);

        // Assert: accepted, not rejected. WorkItemTransitionService short-circuits
        // `item.Status == target` as idempotent before it reaches IsValidTransition, so a repeat
        // of a terminal post is a no-op that reports success.
        //
        // This test previously asserted 400. Status reporting is at-least-once — an agent that
        // crashes after posting will post again on restart — so answering an error for work that
        // already succeeded would push agents into spurious retry and error logging. What has to
        // hold is that the repeat changes nothing, which is the assertion below: the short-circuit
        // returns before the mutation, so the first (real) failure survives.
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // Assert: original error message preserved — the second post must not overwrite it
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        var item = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
        Assert.NotNull(item);
        Assert.Equal("First failure", item.ErrorMessage);
        Assert.Equal(WorkItemStatus.Failed, item.Status);
    }

    [Fact]
    public async Task K8sMode_AgentPostsStatus_NonexistentWorkItem_Returns404OrBadRequest()
    {
        // Arrange: a work item ID that doesn't exist
        var fakeId = Guid.NewGuid();

        // Act
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var statusBody = new { Status = "Running", AgentId = "caa-ghost-pod" };
        var response = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{fakeId}/status", statusBody);

        // Assert: either 404 (not found) or 400 (can't transition what doesn't exist)
        Assert.True(
            response.StatusCode == System.Net.HttpStatusCode.NotFound ||
            response.StatusCode == System.Net.HttpStatusCode.BadRequest,
            $"Expected 404 or 400 for nonexistent work item, got {(int)response.StatusCode}");
    }

    [Fact]
    public async Task K8sMode_AgentFetchesAssignment_NullPayload_Returns404()
    {
        // Arrange: insert a WorkItem with null Payload (corrupted/partial insert)
        var workItemId = Guid.NewGuid();
        await using var db = Fixture.DbContextFactory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "k8s-null-payload-1302",
            IssueProviderConfigId = "issue-e2e",
            Status = WorkItemStatus.Dispatched,
            Payload = null, // Corrupted — no payload
            AgentSelector = "kiro,dotnet",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        });
        await db.SaveChangesAsync();

        // Act
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var response = await httpClient.GetAsync($"/api/work-items/{workItemId}/assignment");

        // Assert: endpoint handles gracefully (404, not 500)
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task K8sMode_AgentFetchesAssignment_NonexistentId_Returns404()
    {
        // Act: request assignment for a work item that doesn't exist
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var fakeId = Guid.NewGuid();
        var response = await httpClient.GetAsync($"/api/work-items/{fakeId}/assignment");

        // Assert
        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G12: Token Refresh Edge Cases — brain tokens, repeated calls, wrong job
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_TokenRefresh_CalledMultipleTimes_AlwaysReturnsToken()
    {
        // Simulates a long-running pipeline that refreshes tokens multiple times
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-k8s-multi-refresh",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Multi-Refresh Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-multi-refresh-token"
            }
        }, CancellationToken.None);

        var workItemId = Guid.NewGuid();
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "k8s-multi-refresh-1400",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        await using var agent = new FakeAgentClient("caa-k8s-multi-refresh", "kiro");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            workItemId.ToString(),
            "k8s-multi-refresh-1400",
            "repo-k8s-multi-refresh");

        // Act: call token refresh 3 times (simulating a long pipeline with multiple git operations)
        var token1 = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);
        var token2 = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);
        var token3 = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);

        // Assert: all succeed with same static token
        Assert.Equal("fake-multi-refresh-token", token1.Token);
        Assert.Equal("fake-multi-refresh-token", token2.Token);
        Assert.Equal("fake-multi-refresh-token", token3.Token);
    }

    [Fact]
    public async Task K8sMode_TokenRefresh_BrainProvider_ReturnsCorrectToken()
    {
        // Arrange: seed both repo and brain provider configs with different tokens
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-k8s-brain-test",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Repo for brain test",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-repo-token"
            }
        }, CancellationToken.None);

        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "brain-k8s-test",
            Kind = ProviderKind.Repository, // Brain is a Repository-kind provider (different repo)
            ProviderType = "GitLab",
            DisplayName = "Brain Knowledge Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-brain-token-different"
            }
        }, CancellationToken.None);

        var workItemId = Guid.NewGuid();
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "k8s-brain-token-1401",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        await using var agent = new FakeAgentClient("caa-k8s-brain-agent", "kiro");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            workItemId.ToString(),
            "k8s-brain-token-1401",
            "repo-k8s-brain-test",
            brainProviderConfigId: "brain-k8s-test");

        // Act: request repo token and brain token
        var repoToken = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);
        var brainToken = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Brain);

        // Assert: different tokens for different scopes
        Assert.Equal("fake-repo-token", repoToken.Token);
        Assert.Equal("fake-brain-token-different", brainToken.Token);
    }

    [Fact]
    public async Task K8sMode_TokenRefresh_WrongJobId_Rejected()
    {
        // Arrange: agent registers with one job but tries to refresh token for a different job
        var realJobId = Guid.NewGuid();
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = realJobId,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = "k8s-wrong-job-1402",
                IssueProviderConfigId = "issue-e2e",
                Status = WorkItemStatus.Running,
                Payload = "{}",
                AgentSelector = "kiro,dotnet",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 3600
            });
            await db.SaveChangesAsync();
        }

        await using var agent = new FakeAgentClient("caa-k8s-wrongjob", "kiro");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            realJobId.ToString(),
            "k8s-wrong-job-1402",
            "repo-e2e");

        // Act: try to refresh token for a DIFFERENT job ID
        var differentJobId = Guid.NewGuid().ToString();
        var exception = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await agent.RequestTokenRefreshAsync(differentJobId, ProviderKind.Repository);
        });

        // Assert: rejected by [RequiresActiveJob] filter — jobId doesn't match ActiveJobId
        Assert.True(
            exception.Message.Contains("not assigned", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains(differentJobId, StringComparison.OrdinalIgnoreCase),
            $"Expected job mismatch rejection, got: {exception.Message}");
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G13: Registration Edge Cases — stale jobs, re-registration
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentRegistersWithStaleCompletedJob_RunNotRestored()
    {
        // Arrange: simulate a completed run in history
        var completedRunId = Guid.NewGuid().ToString();
        await Fixture.HistoryService.AddRunToHistoryAsync(new PipelineRun
        {
            RunId = completedRunId,
            IssueIdentifier = "completed-issue",
            IssueTitle = "Already done",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-e2e",
            StartedAt = DateTime.UtcNow.AddHours(-1)
        });

        // Act: agent registers with a RunId that's already in history (stale pod restart)
        await using var agent = new FakeAgentClient("caa-k8s-stale-agent", "kiro");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            completedRunId,
            "completed-issue",
            "repo-e2e");

        // Assert: agent is registered but NOT busy (stale job ignored)
        var registry = Fixture.AgentRegistry;
        var entry = registry.GetByAgentId("caa-k8s-stale-agent");
        Assert.NotNull(entry);
        // The RegisterAgent handler ignores ActiveJob when RunId is in history
        // Agent should not have ActiveJobId set (run not restored)
        Assert.Null(entry.ActiveJobId);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G15: Authentication — unauthenticated access rejected
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_AgentEndpoints_NoAuth_Returns401()
    {
        // Act: call work-items API without auth header
        using var httpClient = Fixture.CreateApiClient(authenticated: false);
        // Deliberately NO Authorization header

        var response = await httpClient.GetAsync($"/api/work-items/{Guid.NewGuid()}/assignment");

        // Assert: 401 Unauthorized (AgentApiKey scheme rejects)
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task K8sMode_AgentEndpoints_WrongApiKey_Returns401()
    {
        // Act: call with an invalid API key
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", "wrong-key-12345");

        var response = await httpClient.GetAsync($"/api/work-items/{Guid.NewGuid()}/assignment");

        // Assert: 401 Unauthorized
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════════
    // G16: FULL LIFECYCLE — single test exercises the entire K8s agent pipeline
    // This is the "golden path" test that would have caught the original bug
    // (missing RegisterAgent) instantly. Every step is exercised as the real
    // agent would perform it: HTTP + SignalR interleaved, same as production.
    //
    // Flow: Distribute → Dispatch → Agent fetches assignment (HTTP) →
    //       Agent POSTs Running (HTTP) → Agent registers on hub (SignalR) →
    //       Agent refreshes token (SignalR) → Agent POSTs Succeeded (HTTP)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_FullLifecycle_DistributeToCompletion()
    {
        // ── Step 0: Seed a provider config with a token we can verify ──
        await Fixture.ConfigStore.SaveProviderConfigAsync(new ProviderConfig
        {
            Id = "repo-lifecycle-e2e",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Lifecycle E2E Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "fake-lifecycle-token-e2e"
            }
        }, CancellationToken.None);

        // ── Step 1: Distribute via KubernetesWorkDistributor ──
        var distributor = Fixture.Factory.Services.GetRequiredService<IWorkDistributor>();
        var distResult = await distributor.DistributeAsync(new JobDistributionRequest
        {
            IssueIdentifier = "k8s-lifecycle-e2e-9999",
            IssueProviderConfigId = "issue-e2e",
            RepoProviderConfigId = "repo-lifecycle-e2e",
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            TaskType = WorkItemTaskType.Implementation,
            ProjectId = WellKnownIds.DefaultProjectId,
            InitiatedBy = "lifecycle-e2e-test"
        }, CancellationToken.None);

        Assert.True(distResult.Success, $"Distribution failed: {distResult.ErrorMessage}");
        var workItemId = Guid.Parse(distResult.WorkItemId!);

        // Verify: DB has Pending work item with full payload
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            Assert.NotNull(wi);
            Assert.Equal(WorkItemStatus.Pending, wi.Status);
            Assert.NotNull(wi.Payload);
        }

        // ── Step 2: Simulate DispatchService transitioning to Dispatched ──
        var transitionService = Fixture.Factory.Services.GetRequiredService<WorkItemTransitionService>();
        var dispatched = await transitionService.TransitionAsync(workItemId, WorkItemStatus.Dispatched,
            w =>
            {
                w.DispatchedAt = DateTimeOffset.UtcNow;
                w.K8sJobName = $"caa-{workItemId.ToString("N")[..8]}";
            }, ct: CancellationToken.None);
        Assert.True(dispatched, "Pending → Dispatched transition should succeed");

        // ── Step 3: Agent fetches assignment (HTTP — as WorkItemHttpClient does) ──
        using var httpClient = Fixture.CreateApiClient();
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", E2EWebApplicationFactory.TestApiKey);

        var assignResponse = await httpClient.GetAsync($"/api/work-items/{workItemId}/assignment");
        Assert.Equal(System.Net.HttpStatusCode.OK, assignResponse.StatusCode);

        var assignJson = await assignResponse.Content.ReadAsStringAsync();
        using var assignDoc = System.Text.Json.JsonDocument.Parse(assignJson);
        var assignRoot = assignDoc.RootElement;
        Assert.Equal(workItemId.ToString(), assignRoot.GetProperty("jobId").GetString());
        Assert.Equal("repo-lifecycle-e2e", assignRoot.GetProperty("repoProviderConfigId").GetString());

        // ── Step 4: Agent POSTs Running status (HTTP — as WorkItemAgentService does) ──
        var runningResponse = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status",
            new { Status = "Running", AgentId = "caa-lifecycle-pod" });
        Assert.Equal(System.Net.HttpStatusCode.OK, runningResponse.StatusCode);

        // Verify: DB shows Running
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            Assert.Equal(WorkItemStatus.Running, wi!.Status);
            Assert.Equal("caa-lifecycle-pod", wi.AssignedAgentId);
        }

        // ── Step 5: Agent connects to SignalR and registers with ActiveJob ──
        await using var agent = new FakeAgentClient("caa-lifecycle-pod", "kiro", "dotnet");
        await agent.ConnectWithActiveJobAsync(
            AgentHubUrl,
            E2EWebApplicationFactory.TestApiKey,
            workItemId.ToString(),
            "k8s-lifecycle-e2e-9999",
            "repo-lifecycle-e2e");

        // Verify: agent is Busy in registry with correct ActiveJobId
        var registry = Fixture.AgentRegistry;
        var agentEntry = registry.GetByAgentId("caa-lifecycle-pod");
        Assert.NotNull(agentEntry);
        Assert.Equal(AgentStatus.Busy, agentEntry.Status);
        Assert.Equal(workItemId.ToString(), agentEntry.ActiveJobId);

        // ── Step 6: Agent refreshes token (SignalR — the operation that broke in production) ──
        var tokenResponse = await agent.RequestTokenRefreshAsync(workItemId.ToString(), ProviderKind.Repository);
        Assert.NotNull(tokenResponse);
        Assert.Equal("fake-lifecycle-token-e2e", tokenResponse.Token);
        Assert.True(tokenResponse.ExpiresAt > DateTimeOffset.UtcNow, "Token expiry should be in the future");

        // ── Step 7: Agent POSTs Succeeded status with result payload (HTTP) ──
        var resultPayload = System.Text.Json.JsonSerializer.Serialize(new
        {
            FinalStep = "Completed",
            PullRequestUrl = "https://github.com/org/repo/pull/99",
            FilesChangedCount = 7,
            LinesAdded = 200,
            LinesRemoved = 45,
            RetryCount = 0,
            BrainUpdatesPushed = false
        });

        var succeededResponse = await httpClient.PostAsJsonAsync(
            $"/api/work-items/{workItemId}/status",
            new { Status = "Succeeded", AgentId = "caa-lifecycle-pod", Result = resultPayload });
        Assert.Equal(System.Net.HttpStatusCode.OK, succeededResponse.StatusCode);

        // ── Final Assertions: verify complete terminal state ──
        await using (var db = Fixture.DbContextFactory.CreateDbContext())
        {
            var wi = await db.WorkItems.AsNoTracking().FirstOrDefaultAsync(w => w.Id == workItemId);
            Assert.NotNull(wi);
            Assert.Equal(WorkItemStatus.Succeeded, wi.Status);
            Assert.NotNull(wi.CompletedAt);
            Assert.NotNull(wi.DispatchedAt);
            Assert.Equal("caa-lifecycle-pod", wi.AssignedAgentId);
            Assert.NotNull(wi.Result);
            Assert.Contains("pull/99", wi.Result);
        }

        // Verify: the work item is no longer active (can't be re-distributed)
        var statusAfter = await distributor.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);
        Assert.Equal(JobDistributionStatus.Succeeded, statusAfter);

        // Verify: assignment endpoint returns 410 Gone (terminal status)
        var goneResponse = await httpClient.GetAsync($"/api/work-items/{workItemId}/assignment");
        Assert.Equal(System.Net.HttpStatusCode.Gone, goneResponse.StatusCode);
    }
}
