using AwesomeAssertions;
using CodingAgent.Api;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
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
/// by the existing tests in <see cref="CodingAgent.Pipeline.UnitTests.Services.DispatchPendingWorkItemEndpointTests"/>,
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
    private DispatchLifecycleService CreateLifecycleService(IReadOnlyList<string>? pvcPool = null, IKubernetesJobClient? kubeClient = null)
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
            kubeClient ?? Mock.Of<IKubernetesJobClient>(),
            transitionSvc,
            opts);
    }

    // ── BuildConcurrencySnapshotAsync ─────────────────────────────────────────

    [Fact]
    public async Task BuildConcurrencySnapshotAsync_EmptyDb_ReturnsEmptyDictionary()
    {
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        var result = await DispatchWorkItemService.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

        result.Should().BeEmpty("no active items — concurrency map must be empty");
    }

    [Fact]
    public async Task BuildConcurrencySnapshotAsync_WithDispatchedAndRunningItems_CountsByNormalizedSelector()
    {
        var dbFactory = CreateDbFactory();

        // Seed: 2 Dispatched + 1 Running for "kiro,dotnet" (in various orderings)
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "kiro,dotnet");
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "dotnet,kiro"); // same normalized key
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Running, "kiro,dotnet");

        // One Dispatched item for a different selector
        await SeedWorkItemAsync(dbFactory, WorkItemStatus.Dispatched, "opencode,python");

        await using var db = await dbFactory.CreateDbContextAsync();
        var result = await DispatchWorkItemService.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

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
        var dbFactory = CreateDbFactory();

        // Seed items in statuses that must NOT be counted
        foreach (var status in new[] { WorkItemStatus.Pending, WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled })
            await SeedWorkItemAsync(dbFactory, status, "kiro,dotnet");

        await using var db = await dbFactory.CreateDbContextAsync();
        var result = await DispatchWorkItemService.BuildConcurrencySnapshotAsync(db, CancellationToken.None);

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
        // boundary. An off-by-one error in DispatchWorkItemService.IsAtConcurrencyLimit (using >
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

    // ── IsAtConcurrencyLimit ──────────────────────────────────────────────────

    [Fact]
    public void IsAtConcurrencyLimit_MaxConcurrentZero_AlwaysReturnsFalse()
    {
        // maxConcurrent == 0 means "no limit configured" — always allow
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchWorkItemService.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: 0)
            .Should().BeFalse("zero maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NegativeMax_AlwaysReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["selector-1"] = 999 };
        DispatchWorkItemService.IsAtConcurrencyLimit("selector-1", concurrency, maxConcurrent: -1)
            .Should().BeFalse("negative maxConcurrent disables the limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_BelowLimit_ReturnsFalse()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 1 };
        DispatchWorkItemService.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeFalse("current(1) < max(2) — not at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AtLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 2 };
        DispatchWorkItemService.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(2) == max(2) — at limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_AboveLimit_ReturnsTrue()
    {
        var concurrency = new Dictionary<string, int> { ["sel"] = 5 };
        DispatchWorkItemService.IsAtConcurrencyLimit("sel", concurrency, maxConcurrent: 2)
            .Should().BeTrue("current(5) > max(2) — above limit");
    }

    [Fact]
    public void IsAtConcurrencyLimit_SelectorNotInMap_ReturnsFalse()
    {
        // Selector not in the dictionary → GetValueOrDefault returns 0
        var concurrency = new Dictionary<string, int>();
        DispatchWorkItemService.IsAtConcurrencyLimit("new-selector", concurrency, maxConcurrent: 1)
            .Should().BeFalse("no active items for selector → current=0 < max=1");
    }

    [Fact]
    public void IsAtConcurrencyLimit_NullSelector_FallsBackToEmpty_ReturnsFalse()
    {
        // Null agentSelector → "" key, nothing in map → 0 active
        var concurrency = new Dictionary<string, int>();
        DispatchWorkItemService.IsAtConcurrencyLimit(null, concurrency, maxConcurrent: 1)
            .Should().BeFalse("null selector uses empty key, not in map → 0 active");
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

    /// <summary>
    /// A chat has no WorkItem, so the WorkItem claims miss its PVC. A pipeline pod that mounts it while the chat
    /// runs stays Pending (the PVC is ReadWriteOnce), so the preamble leaves out what agent Jobs mount too.
    /// </summary>
    [Fact]
    public async Task BuildDispatchPreambleAsync_LeavesOutThePvcARunningChatJobMounts()
    {
        var chatJob = new k8s.Models.V1Job
        {
            Metadata = new k8s.Models.V1ObjectMeta { Name = "caa-chat-1a2b3c4d" },
            Spec = new k8s.Models.V1JobSpec
            {
                Template = new k8s.Models.V1PodTemplateSpec
                {
                    Spec = new k8s.Models.V1PodSpec
                    {
                        Containers = [],
                        Volumes = [new k8s.Models.V1Volume
                        {
                            Name = "kiro-cli-data",
                            PersistentVolumeClaim = new k8s.Models.V1PersistentVolumeClaimVolumeSource { ClaimName = "pvc-0" }
                        }]
                    }
                }
            },
            Status = new k8s.Models.V1JobStatus()
        };
        var kube = new Mock<IKubernetesJobClient>();
        kube.Setup(k => k.ListJobsAsync("test", AgentJobPvcs.AgentJobSelector, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new k8s.Models.V1JobList { Items = [chatJob] });
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0", "pvc-1"], kubeClient: kube.Object);
        await using var db = await CreateDbFactory().CreateDbContextAsync();

        var (_, pvcResult) = await CreateService().BuildDispatchPreambleAsync(db, lifecycle, CancellationToken.None);

        pvcResult.AvailablePvcs.Should().Equal(["pvc-1"], "a running chat Job mounts pvc-0");
    }

    [Fact]
    public async Task BuildDispatchPreambleAsync_JobsCannotBeRead_UsesTheWorkItemClaims()
    {
        var kube = new Mock<IKubernetesJobClient>();
        kube.Setup(k => k.ListJobsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API server unreachable"));
        var lifecycle = CreateLifecycleService(pvcPool: ["pvc-0", "pvc-1"], kubeClient: kube.Object);
        await using var db = await CreateDbFactory().CreateDbContextAsync();

        var (_, pvcResult) = await CreateService().BuildDispatchPreambleAsync(db, lifecycle, CancellationToken.None);

        pvcResult.AvailablePvcs.Should().Equal(["pvc-0", "pvc-1"]);
    }

    // ── BuildProjectionFromQuickCheck ─────────────────────────────────────────

    /// <summary>
    /// Verifies that <see cref="DispatchWorkItemService.BuildProjectionFromQuickCheck"/> maps the 9 projection
    /// fields (8 from the quick-check row, AgentSelector from normalizedSelector) to the correct <see cref="PendingWorkItemProjection"/> fields.
    /// </summary>
    [Fact]
    public void BuildProjectionFromQuickCheck_MapsAllFieldsCorrectly()
    {
        // Arrange: distinctive values for each of the 9 projection fields
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
        var quickCheck = new DispatchQuickCheck
        {
            Id = id,
            Status = WorkItemStatus.Pending,
            AgentSelector = "dotnet",
            CreatedAt = createdAt,
            TimeoutSeconds = timeoutSeconds,
            TaskType = taskType,
            ProjectId = projectId,
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderConfigId,
            PriorityWeight = priorityWeight
        };
        var projection = DispatchWorkItemService.BuildProjectionFromQuickCheck(quickCheck, normalizedSelector);

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

    // ── TryEnterSelectorDispatchAsync ─────────────────────────────────────────

    /// <summary>
    /// AC4 (issue #3795) — Regression test for the double-dispose risk on the non-Pending branch.
    /// When the item is NOT Pending (so <c>acquiredLock.DisposeAsync()</c> is called inside the
    /// try block) AND that disposal throws, the catch block must NOT call <c>DisposeAsync()</c>
    /// a second time; it must re-throw the original exception.
    ///
    /// <para>
    /// Without the <c>lockTransferred</c> flag fix the catch block calls
    /// <c>acquiredLock.DisposeAsync()</c> unconditionally, so this test would record
    /// <c>Times.Exactly(2)</c> and the <c>Times.Once()</c> assertion would fail.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryEnterSelectorDispatchAsync_NonPendingBranchDisposeThrows_CatchDoesNotDisposeAgainAndRethrows()
    {
        // Arrange — lock handle whose DisposeAsync() throws (simulates a DB connection drop
        // at the moment of advisory-lock release on the non-Pending early-return path).
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync())
            .ThrowsAsync(new InvalidOperationException("simulated disposal failure"));

        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        // Seed a non-Pending work item so the if-branch is entered.
        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                Status = WorkItemStatus.Dispatched,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                AgentSelector = "dotnet,kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act & Assert — original exception from the in-try DisposeAsync() propagates unchanged.
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DispatchWorkItemService.TryEnterSelectorDispatchAsync(
                lockProvider.Object, "dotnet,kiro", db, workItemId, CancellationToken.None));
        ex.Message.Should().Be("simulated disposal failure",
            "the original exception must propagate via 'throw;', not be replaced by a new throw");

        // The critical assertion: DisposeAsync was called exactly once (the in-try call).
        // Without the lockTransferred fix this would be Times.Exactly(2).
        lockHandle.Verify(h => h.DisposeAsync(), Times.Once(),
            "catch block must not call DisposeAsync() a second time when the in-try disposal threw");
    }

    /// <summary>
    /// AC1 — Regression test for issue #3701.
    /// When the post-lock DB query throws after AcquireAsync succeeds, the acquired lock
    /// must be disposed exactly once before the exception propagates.
    /// This test fails on the pre-fix code (no try/catch around the post-lock body) and passes
    /// after the fix is applied.
    ///
    /// <para>
    /// The post-lock query is made to throw by passing a pre-cancelled CancellationToken.
    /// The AcquireAsync mock is set up to succeed regardless (returns a completed task),
    /// so the lock IS acquired. The in-memory provider then checks <c>ct.ThrowIfCancellationRequested()</c>
    /// at the start of query execution and throws <c>OperationCanceledException</c>.
    /// The bare <c>catch</c> block in the fix handles all exception types, so this correctly
    /// tests the lock-disposal behavior. No native SQLite library is used, avoiding
    /// process-crash interactions with Coverlet's coverage finalization.
    /// </para>
    /// </summary>
    [Fact]
    public async Task TryEnterSelectorDispatchAsync_PostLockQueryThrows_DisposesLockExactlyOnce()
    {
        // Arrange — trackable lock handle
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // AcquireAsync mock returns a completed task, so it succeeds even for a cancelled token.
        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        // Use EF in-memory provider — no native SQLite library required.
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Pre-cancel the token. AcquireAsync completes (mock ignores the token);
        // the post-lock FirstOrDefaultAsync then calls ThrowIfCancellationRequested()
        // and throws OperationCanceledException.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Act & Assert
        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            DispatchWorkItemService.TryEnterSelectorDispatchAsync(
                lockProvider.Object, "dotnet,kiro", db, Guid.NewGuid(), cts.Token));

        lockHandle.Verify(h => h.DisposeAsync(), Times.Once(),
            "lock must be disposed exactly once on the exception path");
    }

    /// <summary>
    /// AC2 — Caller-ownership path.
    /// When the item is Pending, TryEnterSelectorDispatchAsync must return (lockHandle, null)
    /// without calling DisposeAsync inside the method — the caller holds dispose responsibility.
    /// </summary>
    [Fact]
    public async Task TryEnterSelectorDispatchAsync_ItemIsPending_ReturnsLockWithoutDisposing()
    {
        // Arrange
        var lockHandle = new Mock<IAsyncDisposable>();
        lockHandle.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(lockHandle.Object);

        var dbFactory = CreateDbFactory();
        var workItemId = Guid.NewGuid();
        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                Status = WorkItemStatus.Pending,
                TaskType = WorkItemTaskType.Implementation,
                IssueIdentifier = $"issue-{Guid.NewGuid():N}",
                IssueProviderConfigId = "prov-1",
                AgentSelector = "dotnet,kiro",
                TimeoutSeconds = 3600,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = "{}"
            });
            await seedCtx.SaveChangesAsync();
        }

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (returnedLock, earlyReturn) = await DispatchWorkItemService.TryEnterSelectorDispatchAsync(
            lockProvider.Object, "dotnet,kiro", db, workItemId, CancellationToken.None);

        // Assert — caller receives the lock handle; DisposeAsync is NOT called inside the method
        returnedLock.Should().BeSameAs(lockHandle.Object,
            "the lock handle must be returned to the caller, not kept internally");
        earlyReturn.Should().BeNull(
            "a Pending item has no early-return reason — the caller proceeds to dispatch");
        lockHandle.Verify(h => h.DisposeAsync(), Times.Never(),
            "caller owns the lock on the Pending success path — DisposeAsync must not be called inside TryEnterSelectorDispatchAsync");
    }

    // ── PrepareDispatchVariantAsync ───────────────────────────────────────────

    /// <summary>
    /// Regression test for issue #3758 (AC3).
    /// When the work item has a non-null <see cref="WorkItemEntity.ProjectId"/>, and the project
    /// row exists in the database with a <c>Secrets</c> entry in its Settings JSON,
    /// <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must return
    /// <c>shouldContinue = true</c> and a non-null <c>projectSecrets</c> dictionary populated
    /// with the project's secrets.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNonNullProjectId_PopulatesProjectSecretsFromDb()
    {
        // Arrange — seed a ProjectEntity with a Secrets entry in its Settings JSON.
        var projectId = Guid.NewGuid();
        var dbFactory = CreateDbFactory();

        await using (var seedCtx = await dbFactory.CreateDbContextAsync())
        {
            seedCtx.Projects.Add(new ProjectEntity
            {
                Id = projectId,
                Name = "Test Project",
                Enabled = true,
                Settings = "{\"Secrets\":{\"MY_KEY\":\"my_value\"}}"
            });
            await seedCtx.SaveChangesAsync();
        }

        // WorkItemEntity is constructed in memory — PrepareDispatchVariantAsync only reads
        // workItem.ProjectId and calls DispatchLifecycleService.LoadProjectSecretsAsync(db, ...)
        // which queries db.Projects. It never queries db.WorkItems, so no DB insert is needed.
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"secrets-test-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = projectId
        };

        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("the variant preparation must proceed when ProjectId is set");
        projectSecrets.Should().NotBeNull("a project with a Secrets entry must produce a non-null dictionary");
        projectSecrets!.Should().ContainKey("MY_KEY", "the key from the project's Secrets JSON must be present");
        projectSecrets["MY_KEY"].Should().Be("my_value", "the value must match the project's Secrets JSON");
    }

    /// <summary>
    /// Complementary test: when <see cref="WorkItemEntity.ProjectId"/> is null,
    /// <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must return
    /// <c>shouldContinue = true</c> and a null <c>projectSecrets</c> — the project-secrets
    /// loading branch is not entered.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNullProjectId_ReturnsNullSecrets()
    {
        // Arrange — work item with no project association.
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"no-project-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = null
        };

        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("null ProjectId is not a failure — dispatch proceeds normally");
        projectSecrets.Should().BeNull("no project is associated — projectSecrets must remain null");
    }

    /// <summary>
    /// Complementary test: when <see cref="WorkItemEntity.ProjectId"/> is non-null but the
    /// referenced project row does not exist in the database, <c>LoadProjectSecretsAsync</c>
    /// returns null via the <c>FirstOrDefaultAsync</c> → <c>settingsJson is null</c> exit path,
    /// and <see cref="WorkItemDispatchEndpoints.PrepareDispatchVariantAsync"/> must therefore
    /// return <c>shouldContinue = true</c> and a null <c>projectSecrets</c>.
    /// A regression that returns an empty dictionary or throws instead of null would be caught here.
    /// </summary>
    [Fact]
    public async Task PrepareDispatchVariantAsync_WithNonNullProjectId_ProjectNotInDb_ReturnsNullSecrets()
    {
        // Arrange — ProjectId points to a GUID that has no corresponding row in the DB.
        var missingProjectId = Guid.NewGuid();
        var workItem = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"missing-project-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}",
            ProjectId = missingProjectId
        };

        // Empty DB — no ProjectEntity for missingProjectId.
        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (shouldContinue, projectSecrets) = await WorkItemDispatchEndpoints
            .PrepareDispatchVariantAsync(db, workItem, CancellationToken.None);

        // Assert
        shouldContinue.Should().BeTrue("a missing project row is not a hard failure — dispatch proceeds");
        projectSecrets.Should().BeNull(
            "LoadProjectSecretsAsync returns null when FirstOrDefaultAsync finds no project row");
    }

    /// <summary>
    /// AC3 — TimeoutException path unchanged.
    /// When AcquireAsync throws TimeoutException the method must return (null, 503) without
    /// any IAsyncDisposable having been acquired, and the new try/catch must not absorb or
    /// wrap the TimeoutException.
    /// </summary>
    [Fact]
    public async Task TryEnterSelectorDispatchAsync_LockAcquisitionTimesOut_Returns503WithoutDispose()
    {
        // Arrange
        var lockProvider = new Mock<IDistributedLockProvider>();
        lockProvider
            .Setup(p => p.AcquireAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("lock timed out"));

        var dbFactory = CreateDbFactory();
        await using var db = await dbFactory.CreateDbContextAsync();

        // Act
        var (lockHandleResult, earlyReturn) = await DispatchWorkItemService.TryEnterSelectorDispatchAsync(
            lockProvider.Object, "dotnet,kiro", db, Guid.NewGuid(), CancellationToken.None);

        // Assert
        lockHandleResult.Should().BeNull("no lock was acquired — nothing to dispose");
        earlyReturn.Should().NotBeNull("a lock timeout must produce a non-null early-return result");
        var statusResult = earlyReturn as Microsoft.AspNetCore.Http.HttpResults.StatusCodeHttpResult;
        statusResult.Should().NotBeNull("lock timeout must return a StatusCodeHttpResult");
        statusResult!.StatusCode.Should().Be(503,
            "TimeoutException on lock acquisition maps to 503 Service Unavailable");
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
