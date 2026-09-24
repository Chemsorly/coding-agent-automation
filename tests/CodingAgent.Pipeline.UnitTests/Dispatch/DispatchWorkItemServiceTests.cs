using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchWorkItemService"/> — the shared helper class extracted
/// from <see cref="WorkItemDispatchEndpoints"/> by issue #2743.
///
/// <para>
/// These tests target the service methods directly. The handler-level behaviour is covered
/// by the existing tests in <see cref="CodingAgent.Pipeline.UnitTests.Services.SynchronousDispatchEndpointTests"/>,
/// which continue to exercise the full dispatch pipeline (including the new service).
/// </para>
/// </summary>
public sealed class DispatchWorkItemServiceTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DispatchWorkItemService CreateService(
        string labels = "kiro,dotnet",
        int maxConcurrent = 5,
        string providerType = "kiro") =>
        new DispatchWorkItemService(CreateTemplateStore(labels, maxConcurrent, providerType));

    private static JobTemplateStore CreateTemplateStore(
        string labels = "kiro,dotnet",
        int maxConcurrent = 5,
        string providerType = "kiro") =>
        JobTemplateStore.LoadFromYaml($"""
            - labels: "{labels}"
              image: "test-image:latest"
              imagePullPolicy: "Always"
              providerType: "{providerType}"
              maxConcurrent: {maxConcurrent}
            """);

    private static IDbContextFactory<PipelineDbContext> CreateDbFactory(string? dbName = null)
    {
        var name = dbName ?? $"dispatch-svc-test-{Guid.NewGuid():N}";
        var opts = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(name)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SimpleInMemoryDbContextFactory(opts);
    }

    private static PvcAvailabilityResult MakePvcResult(int available, int claimed = 0) =>
        new PvcAvailabilityResult(
            Enumerable.Range(0, available).Select(i => $"pvc-{i}").ToList(),
            claimed);

    private static JobTemplate ResolveTemplate(JobTemplateStore store, string selector = "kiro,dotnet")
        => store.Resolve(JobTemplateStore.NormalizeLabels(selector))!;

    private static async Task SeedWorkItemAsync(
        IDbContextFactory<PipelineDbContext> dbFactory,
        WorkItemStatus status,
        string selector)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = status,
            AgentSelector = selector,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
    }

    // TODO [WARNING]: CreateLifecycleService constructs its own independent CreateDbFactory(),
    // so the lifecycle service's internal WorkItemTransitionService queries a completely
    // separate in-memory database from the one used in test bodies. The PVC availability
    // query (QueryAvailablePvcsAsync) uses the caller-supplied db argument directly, so
    // the test currently passes correctly — but the isolation between the two factories
    // creates a latent confusion that could mask a regression if the plumbing changes.
    // The lifecycle service's private dbFactory is dead weight here.
    private DispatchLifecycleService CreateLifecycleService(IReadOnlyList<string>? pvcPool = null)
    {
        var dbFactory = CreateDbFactory();
        var transitionSvc = new WorkItemTransitionService(
            dbFactory,
            Mock.Of<ILogger<WorkItemTransitionService>>());
        var opts = new DispatchServiceOptions
        {
            Namespace = "test",
            OrchestratorUrl = "http://test",
            AgentApiKeySecretName = "secret",
            AgentServiceAccountName = "sa",
            KiroPvcPool = (pvcPool ?? ["pvc-0", "pvc-1"]).ToList()
        };
        return new DispatchLifecycleService(
            Mock.Of<IKubernetesJobClient>(),
            transitionSvc,
            opts);
    }

    // ── BuildConcurrencySnapshotAsync ─────────────────────────────────────────

    [Fact]
    public async Task BuildConcurrencySnapshotAsync_EmptyDb_ReturnsEmptyDictionary()
    {
        var svc = CreateService();
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        var result = await svc.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

        result.Should().BeEmpty("no active items — concurrency map must be empty");
    }

    [Fact]
    public async Task BuildConcurrencySnapshotAsync_WithDispatchedAndRunningItems_CountsByNormalizedSelector()
    {
        var svc = CreateService();
        var dbFactory = CreateDbFactory();

        // Seed: 2 Dispatched + 1 Running for "kiro,dotnet" (in various orderings)
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "kiro,dotnet");
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "dotnet,kiro"); // same normalized key
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Running, "kiro,dotnet");

        // One Dispatched item for a different selector
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "opencode,python");

        await using var db = await dbFactory.CreateDbContextAsync();
        var result = await svc.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

        // "dotnet,kiro" == "kiro,dotnet" after NormalizeLabels (sorted)
        var normalizedKiroDotnet = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        result.Should().ContainKey(normalizedKiroDotnet);
        result[normalizedKiroDotnet].Should().Be(3,
            "2 Dispatched + 1 Running for the kiro,dotnet selector (both orderings normalize to the same key)");

        var normalizedOpencode = JobTemplateStore.NormalizeLabels("opencode,python");
        result.Should().ContainKey(normalizedOpencode);
        result[normalizedOpencode].Should().Be(1);
        // TODO [WARNING]: No assertion on the total entry count. If the implementation stored both
        // the raw and normalized keys (a double-count bug), result[normalizedKiroDotnet] would still
        // be 3 while phantom entries existed. Add: result.Should().HaveCount(2) to close this gap.
    }

    [Fact]
    public async Task BuildConcurrencySnapshotAsync_IgnoresPendingAndTerminalStatuses()
    {
        var svc = CreateService();
        var dbFactory = CreateDbFactory();

        // Seed items in statuses that must NOT be counted
        foreach (var status in new[] { WorkItemStatus.Pending, WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled })
            await SeedWorkItemAsync(dbFactory, status, "kiro,dotnet");

        await using var db = await dbFactory.CreateDbContextAsync();
        var result = await svc.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

        result.Should().BeEmpty(
            "Pending, Succeeded, Failed, and Cancelled items must not appear in the concurrency snapshot");
    }

    // ── ApplyGates ────────────────────────────────────────────────────────────

    [Fact]
    public void ApplyGates_ConcurrencyLimitReached_Returns409()
    {
        var svc = CreateService(maxConcurrent: 2);
        var template = ResolveTemplate(CreateTemplateStore(maxConcurrent: 2));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [normalizedSelector] = 2  // at limit
        };
        var pvcResult = MakePvcResult(available: 2);

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: true,
            callerName: "Test");

        result.Should().NotBeNull("concurrency limit reached must fire the gate");
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "concurrency gate must return 409 Conflict");
        // TODO [WARNING]: The response body (Conflict<string>.Value) is not asserted here.
        // ApplyGates encodes the selector and current/max counts in the message literal
        // (e.g. "Concurrency limit reached for selector 'dotnet,kiro' (2/2)."). A bug that
        // swaps sanitizedSelector with normalizedSelector, omits the counts, or returns the
        // wrong literal would be invisible to this test. Add: conflict.Value.Should().Contain(...)
    }

    [Fact]
    public void ApplyGates_KiroAgentNoPvc_Returns503()
    {
        var svc = CreateService(maxConcurrent: 5);
        var template = ResolveTemplate(CreateTemplateStore(maxConcurrent: 5));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal); // empty — no active items
        var pvcResult = MakePvcResult(available: 0);  // no PVCs

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: true,
            callerName: "Test");

        result.Should().NotBeNull("PVC exhaustion for a kiro agent must fire the gate");
        var statusResult = result as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull();
        statusResult!.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable,
            "PVC gate must return 503");
    }

    [Fact]
    public void ApplyGates_AllGatesPass_ReturnsNull()
    {
        // TODO [WARNING]: This test uses maxConcurrent=5 with count=1, which is far from the
        // boundary. An off-by-one error in DispatchStateBuilder.IsAtConcurrencyLimit (using >
        // instead of >=) would not be caught here. Add a complementary test that uses
        // maxConcurrent=2 with count=1 (exactly one below the limit) and asserts null is returned,
        // to constrain the comparison operator and catch off-by-one regressions at the boundary.
        var svc = CreateService(maxConcurrent: 5);
        var template = ResolveTemplate(CreateTemplateStore(maxConcurrent: 5));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [normalizedSelector] = 1  // below limit
        };
        var pvcResult = MakePvcResult(available: 1);

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: true,
            callerName: "Test");

        result.Should().BeNull("all gates pass — must return null (pass-through)");
    }

    [Fact]
    public void ApplyGates_NonKiroAgentNoPvc_ReturnsNull()
    {
        var svc = CreateService(providerType: "opencode", maxConcurrent: 5);
        var template = ResolveTemplate(CreateTemplateStore("kiro,dotnet", 5, "opencode"));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal);
        var pvcResult = MakePvcResult(available: 0);  // no PVCs — but irrelevant for non-kiro

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: false,
            callerName: "Test");

        result.Should().BeNull(
            "non-kiro agents skip the PVC gate — zero PVCs must not block dispatch");
    }

    [Fact]
    public void ApplyGates_MaxConcurrentZero_TreatsAsUnlimited_ReturnsNull()
    {
        var svc = CreateService(maxConcurrent: 0);  // 0 means unlimited
        var template = ResolveTemplate(CreateTemplateStore(maxConcurrent: 0));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        // Even with many active items, maxConcurrent=0 must not fire the gate
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [normalizedSelector] = 100
        };
        var pvcResult = MakePvcResult(available: 1);

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: true,
            callerName: "Test");

        result.Should().BeNull("maxConcurrent=0 means unlimited — must always pass the concurrency gate");
    }

    [Fact]
    public void ApplyGates_ConcurrencyLimitReached_WhenPvcPoolAlsoEmpty_Returns409()
    {
        // Both conditions hold simultaneously: concurrency at limit AND zero available PVCs.
        // ApplyGates checks concurrency first, so it must return 409 Conflict (not 503).
        // This confirms the gate ordering is authoritative and that the concurrency gate
        // short-circuits before the PVC gate — critical for correct PvcPoolExhaustions attribution.
        var svc = CreateService(maxConcurrent: 2);
        var template = ResolveTemplate(CreateTemplateStore(maxConcurrent: 2));
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        var concurrencyBySelector = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            [normalizedSelector] = 2  // at limit
        };
        var pvcResult = MakePvcResult(available: 0);  // also empty

        var result = svc.ApplyGates(
            normalizedSelector, "kiro,dotnet",
            concurrencyBySelector, pvcResult, template, isKiroAgent: true,
            callerName: "Test");

        result.Should().NotBeNull("combined condition must fire a gate");
        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "concurrency gate fires first and must return 409 Conflict, not 503, even when PVC pool is also empty");
    }

    // ── CreateWorkItemEntity ──────────────────────────────────────────────────

    [Fact]
    public void CreateWorkItemEntity_WithStatusPending_HasNullDispatchedAt()
    {
        var workItemId = Guid.NewGuid();
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("test-issue-1"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            RunId = Guid.NewGuid().ToString()
        };
        const string payloadJson = "{}";

        var entity = DispatchWorkItemService.CreateWorkItemEntity(
            workItemId, request, WorkItemStatus.Pending, dispatchedAt: null, payloadJson);

        entity.Id.Should().Be(workItemId);
        entity.Status.Should().Be(WorkItemStatus.Pending);
        entity.DispatchedAt.Should().BeNull("Pending path does not set DispatchedAt");
        entity.IssueIdentifier.Should().Be("test-issue-1");
        entity.TaskType.Should().Be(WorkItemTaskType.Implementation);
        entity.AgentSelector.Should().Be(JobTemplateStore.NormalizeLabels("kiro,dotnet"));
        entity.TimeoutSeconds.Should().Be(3600);
        entity.Payload.Should().Be(payloadJson);
        // TODO [WARNING]: Missing assertions for CreatedAt, IssueProviderConfigId, and ProjectId.
        // CreatedAt is a required persistence field — a regression setting it to default(DateTimeOffset)
        // would cause DB constraint failures and would not be caught here.
        // IssueProviderConfigId and ProjectId are identity fields used in the partial unique index
        // guarding against duplicate live items; a mapping omission would cause silent data corruption.
    }

    [Fact]
    public void CreateWorkItemEntity_WithStatusDispatched_PopulatesDispatchedAt()
    {
        var workItemId = Guid.NewGuid();
        var dispatchedAt = DateTimeOffset.UtcNow;
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("test-issue-2"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "dotnet,kiro",
            TimeoutSeconds = 7200,
            RunId = Guid.NewGuid().ToString()
        };
        const string payloadJson = "{\"schema\":1}";

        var entity = DispatchWorkItemService.CreateWorkItemEntity(
            workItemId, request, WorkItemStatus.Dispatched, dispatchedAt, payloadJson);

        entity.Status.Should().Be(WorkItemStatus.Dispatched);
        entity.DispatchedAt.Should().Be(dispatchedAt);
        entity.AgentSelector.Should().Be(JobTemplateStore.NormalizeLabels("dotnet,kiro"),
            "AgentSelector must be normalized");
    }

    [Fact]
    public void CreateWorkItemEntity_ManualInitiatedBy_SetsPriorityWeight100()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("manual-issue"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = InitiatedByConstants.Manual, // manual dispatch
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            RunId = Guid.NewGuid().ToString()
        };

        var entity = DispatchWorkItemService.CreateWorkItemEntity(
            Guid.NewGuid(), request, WorkItemStatus.Pending, null, "{}");

        entity.PriorityWeight.Should().Be(100,
            "manually-initiated dispatches receive priority weight 100");
    }

    [Fact]
    public void CreateWorkItemEntity_AutoInitiatedBy_SetsPriorityWeightZero()
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("auto-issue"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "closed-loop", // automated
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 3600,
            RunId = Guid.NewGuid().ToString()
        };

        var entity = DispatchWorkItemService.CreateWorkItemEntity(
            Guid.NewGuid(), request, WorkItemStatus.Pending, null, "{}");

        entity.PriorityWeight.Should().Be(0,
            "closed-loop dispatches receive priority weight 0");
        // TODO [WARNING]: No test covers the TraceParent field of CreateWorkItemEntity.
        // The factory has a two-branch expression:
        //   request.TraceContext?.GetValueOrDefault("traceparent") ?? PipelineTelemetry.FormatTraceParent(Activity.Current)
        // Neither the "trace context provided" branch nor the "fall back to Activity.Current" branch
        // is exercised by any test. A regression in either branch (wrong key, wrong fallback) would
        // be undetected. Add tests for both the populated-TraceContext and the null-TraceContext paths.
    }

    // ── HandleUniqueViolationFallback ─────────────────────────────────────────

    [Fact]
    public void HandleUniqueViolationFallback_ReturnsConflictWithExpectedMessage()
    {
        var result = DispatchWorkItemService.HandleUniqueViolationFallback();

        result.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.Conflict<string>>(
            "the fallback must produce a 409 Conflict");

        var conflict = (Microsoft.AspNetCore.Http.HttpResults.Conflict<string>)result;
        conflict.Value.Should().Be("A live work item already exists for this issue.",
            "the exact literal must match what callers previously inlined");
    }

    // ── BuildDispatchPreambleAsync ────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DispatchWorkItemService.BuildDispatchPreambleAsync"/> returns
    /// a concurrency dictionary with the correct active-item counts and a PVC result that
    /// correctly reflects availability, using the same <see cref="PipelineDbContext"/> for both
    /// inner calls.
    /// </summary>
    [Fact]
    public async Task BuildDispatchPreambleAsync_ReturnsConcurrencySnapshotAndPvcResult()
    {
        // Arrange: seed 2 active items for "kiro,dotnet" and one active item claiming "pvc-0"
        var dbFactory = CreateDbFactory();
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "kiro,dotnet");
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Running, "kiro,dotnet");
        // This active item claims "pvc-0", leaving only "pvc-1" available from the pool ["pvc-0","pvc-1"]
        await using (var seedDb = await dbFactory.CreateDbContextAsync())
        {
            seedDb.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"pvc-holder-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro,dotnet",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}",
                ClaimedPvcName = "pvc-0"
            });
            await seedDb.SaveChangesAsync();
        }

        var svc = CreateService(maxConcurrent: 5);
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0", "pvc-1"]);

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (concurrencyBySelector, pvcResult) = await svc.BuildDispatchPreambleAsync(db, lifecycle, CancellationToken.None);

        // Assert — concurrency snapshot
        var normalizedSelector = JobTemplateStore.NormalizeLabels("kiro,dotnet");
        concurrencyBySelector.Should().ContainKey(normalizedSelector);
        // 2 seeded + 1 pvc-holder = 3 active items for "kiro,dotnet"
        // TODO [WARNING]: "2 unseeded" in the original comment was a typo — the items are seeded
        // (via SeedWorkItemAsync). Additionally, there is no assertion on the total number of keys
        // in concurrencyBySelector. If the implementation accidentally emits both raw and normalized
        // keys (a double-count bug), this assertion would still pass while a phantom extra key existed.
        // Add: concurrencyBySelector.Should().HaveCount(1) to close the gap.
        concurrencyBySelector[normalizedSelector].Should().Be(3,
            "3 Dispatched/Running items exist for kiro,dotnet");

        // Assert — PVC result: "pvc-0" is claimed, "pvc-1" is available
        // TODO [WARNING]: No complementary test exists for the fully-claimed case (all PVCs have
        // active ClaimedPvcName entries). A regression in QueryAvailablePvcsAsync returning a
        // non-empty list when all PVCs are claimed would flow silently past this test.
        // TODO [WARNING]: There is no assertion that both inner queries (BuildConcurrencySnapshotAsync
        // and QueryAvailablePvcsAsync) execute on the same PipelineDbContext instance that was passed
        // in. The XML doc on BuildDispatchPreambleAsync states "must be the same context used for any
        // subsequent calls on this request" — this guarantee is untested.
        pvcResult.AvailablePvcs.Should().HaveCount(1,
            "pvc-0 is claimed; only pvc-1 is available");
        pvcResult.AvailablePvcs.Should().Contain("pvc-1");
        pvcResult.ClaimedCount.Should().Be(1, "one PVC (pvc-0) is claimed");
    }

    // ── BuildProjectionFromEntity ─────────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DispatchWorkItemService.BuildProjectionFromEntity"/> maps all 9
    /// fields of <see cref="PendingWorkItemProjection"/> correctly from a <see cref="WorkItemEntity"/>.
    /// </summary>
    [Fact]
    public void BuildProjectionFromEntity_MapsAllFieldsCorrectly()
    {
        // Arrange: a WorkItemEntity with distinctive values for every mapped field
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2025, 6, 15, 10, 30, 0, TimeSpan.Zero);
        var entity = new WorkItemEntity
        {
            Id = id,
            AgentSelector = "dotnet,kiro",
            CreatedAt = createdAt,
            TimeoutSeconds = 7200,
            TaskType = WorkItemTaskType.Review,
            ProjectId = projectId,
            IssueIdentifier = "GH-42",
            IssueProviderConfigId = "provider-abc",
            PriorityWeight = 100,
            // Additional fields on WorkItemEntity that must NOT affect the projection
            Status = WorkItemStatus.Dispatched,
            Payload = "{}"
        };

        // Act
        var projection = DispatchWorkItemService.BuildProjectionFromEntity(entity);

        // Assert: all 9 fields mapped correctly
        projection.Id.Should().Be(id, "Id must come from entity.Id");
        projection.AgentSelector.Should().Be("dotnet,kiro", "AgentSelector must come from entity.AgentSelector");
        projection.CreatedAt.Should().Be(createdAt, "CreatedAt must come from entity.CreatedAt");
        projection.TimeoutSeconds.Should().Be(7200, "TimeoutSeconds must come from entity.TimeoutSeconds");
        projection.TaskType.Should().Be(WorkItemTaskType.Review, "TaskType must come from entity.TaskType");
        projection.ProjectId.Should().Be(projectId, "ProjectId must come from entity.ProjectId");
        projection.IssueIdentifier.Should().Be("GH-42", "IssueIdentifier must come from entity.IssueIdentifier");
        projection.IssueProviderConfigId.Should().Be("provider-abc", "IssueProviderConfigId must come from entity.IssueProviderConfigId");
        projection.PriorityWeight.Should().Be(100, "PriorityWeight must come from entity.PriorityWeight");
    }

    // ── BuildProjectionFromQuickCheck ─────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DispatchWorkItemService.BuildProjectionFromQuickCheck"/> maps all 9
    /// explicit parameters to the correct <see cref="PendingWorkItemProjection"/> fields.
    /// </summary>
    [Fact]
    public void BuildProjectionFromQuickCheck_MapsAllFieldsCorrectly()
    {
        // Arrange: distinctive values for each of the 9 parameters
        var id = Guid.NewGuid();
        var projectId = Guid.NewGuid();
        var createdAt = new DateTimeOffset(2025, 3, 10, 8, 0, 0, TimeSpan.Zero);
        const string normalizedSelector = "dotnet,kiro";
        const int timeoutSeconds = 3600;
        const WorkItemTaskType taskType = WorkItemTaskType.Implementation;
        const string issueIdentifier = "GH-99";
        const string issueProviderConfigId = "prov-xyz";
        const int priorityWeight = 50;

        // Act
        var projection = DispatchWorkItemService.BuildProjectionFromQuickCheck(
            id: id,
            normalizedSelector: normalizedSelector,
            createdAt: createdAt,
            timeoutSeconds: timeoutSeconds,
            taskType: taskType,
            projectId: projectId,
            issueIdentifier: issueIdentifier,
            issueProviderConfigId: issueProviderConfigId,
            priorityWeight: priorityWeight);

        // Assert: all 9 fields mapped correctly
        projection.Id.Should().Be(id);
        projection.AgentSelector.Should().Be(normalizedSelector, "AgentSelector must come from normalizedSelector parameter");
        projection.CreatedAt.Should().Be(createdAt);
        projection.TimeoutSeconds.Should().Be(timeoutSeconds);
        projection.TaskType.Should().Be(taskType);
        projection.ProjectId.Should().Be(projectId);
        projection.IssueIdentifier.Should().Be(issueIdentifier);
        projection.IssueProviderConfigId.Should().Be(issueProviderConfigId);
        projection.PriorityWeight.Should().Be(priorityWeight);
    }
}

// ── Test infrastructure ──────────────────────────────────────────────────────

file sealed class SimpleInMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly DbContextOptions<PipelineDbContext> _opts;
    public SimpleInMemoryDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
    public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult<PipelineDbContext>(new TestPipelineDbContext(_opts));
}

file sealed class TestPipelineDbContext : PipelineDbContext
{
    public TestPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }
    protected override void OnModelCreating(ModelBuilder mb)
    {
        base.OnModelCreating(mb);
        foreach (var et in mb.Model.GetEntityTypes())
        {
            var rv = et.FindProperty("RowVersion");
            if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
        }
        foreach (var et in mb.Model.GetEntityTypes())
        {
            foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                et.RemoveIndex(idx);
        }
    }
}
