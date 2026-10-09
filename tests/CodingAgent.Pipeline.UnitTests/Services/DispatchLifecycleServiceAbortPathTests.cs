using AwesomeAssertions;
using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using k8s.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for the abort paths of
/// <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>.
///
/// <para>
/// Issue #3496: None of the early-return branches — empty PVC pool, stale WorkItem status,
/// throwing or aborting <c>prepareVariant</c>, concurrency/generic failure on the
/// <c>K8sJobName</c> pre-write, or the concurrency swallow on the final <c>Dispatched</c>
/// write — were covered by any existing test. This class pins every one of those branches
/// so that removing a guard or a PVC release call causes at least one test here to fail.
/// </para>
///
/// <para>
/// Infrastructure note: the EF Core InMemory provider cannot raise
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/> or
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> on its own.
/// <see cref="FaultInjectingPipelineDbContext"/> injects exceptions into
/// <see cref="Microsoft.EntityFrameworkCore.DbContext.SaveChangesAsync(System.Threading.CancellationToken)"/>
/// on the N-th call to simulate those paths without touching a real database.
/// </para>
/// </summary>
public sealed class DispatchLifecycleServiceAbortPathTests
{
    // Each test class instance gets its own isolated InMemory database.
    private readonly DbContextOptions<PipelineDbContext> _options =
        new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"abort-path-test-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    // Loose mock: unexpected calls do not throw — Times.Never / Times.Once verify them.
    private readonly Mock<IKubernetesJobClient> _k8s = new();

    // Tracks how many times Prepare() was invoked across the current test instance.
    private int _prepareCalls;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a <see cref="DispatchLifecycleService"/> wired to the shared InMemory database
    /// and the shared K8s mock. Callers write <c>using var service = CreateService();</c>.
    /// </summary>
    private DispatchLifecycleService CreateService()
    {
        var transitionSvc = new WorkItemTransitionService(
            new FaultInjectingDbContextFactory(_options),
            Mock.Of<ILogger<WorkItemTransitionService>>());
        var opts = new DispatchServiceOptions
        {
            Namespace = "test-ns",
            OrchestratorUrl = "http://test",
            AgentApiKeySecretName = "agent-key",
            AgentApiKeyValue = "test-master-key",
            AgentServiceAccountName = "sa",
            KiroPvcPool = ["pvc-0"]
        };
        return new DispatchLifecycleService(_k8s.Object, transitionSvc, opts);
    }

    /// <summary>
    /// Seeds a <see cref="WorkItemEntity"/> with the given <paramref name="status"/> using a
    /// fresh context instance so the seeding save does NOT increment any dispatch-context counter.
    /// </summary>
    private async Task<WorkItemEntity> SeedWorkItemAsync(WorkItemStatus status)
    {
        var entity = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"issue-{Guid.NewGuid():N}",
            IssueProviderConfigId = "prov-1",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro,dotnet",
            Status = status,
            TimeoutSeconds = 3600,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = "{}"
        };
        await using var db = new FaultInjectingPipelineDbContext(_options);
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();
        return entity;
    }

    /// <summary>
    /// Builds a <see cref="DispatchLifecycleContext"/> for <paramref name="entity"/> using
    /// the provided <paramref name="db"/> instance (whose fault-injection state carries across
    /// the dispatch run) and the supplied PVC list and concurrency map.
    /// <c>ExpectedInitialStatus</c> defaults to <see cref="WorkItemStatus.Pending"/>.
    /// </summary>
    private static DispatchLifecycleContext BuildContext(
        PipelineDbContext db,
        WorkItemEntity entity,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector)
    {
        var projection = new PendingWorkItemProjection
        {
            Id = entity.Id,
            AgentSelector = entity.AgentSelector,
            CreatedAt = entity.CreatedAt,
            TimeoutSeconds = entity.TimeoutSeconds,
            TaskType = entity.TaskType
        };
        var template = new JobTemplate
        {
            Labels = "kiro,dotnet",
            Image = "test-image:latest",
            ProviderType = "kiro",
            MaxConcurrent = 5
        };
        return new DispatchLifecycleContext(
            db,
            projection,
            template,
            IsKiro: true,
            AvailablePvcs: availablePvcs,
            ConcurrencyBySelector: concurrencyBySelector,
            LogPrefix: "test ");
    }

    /// <summary>
    /// Returns a <c>prepareVariant</c> delegate that increments <see cref="_prepareCalls"/>
    /// and returns <c>(shouldContinue, null)</c>.
    /// </summary>
    private Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>>
        Prepare(bool shouldContinue) =>
        _ =>
        {
            _prepareCalls++;
            return Task.FromResult((shouldContinue, (Dictionary<string, string>?)null));
        };

    /// <summary>
    /// Re-reads <paramref name="id"/> from the InMemory database with
    /// <c>AsNoTracking</c> to bypass the change tracker's cached state.
    /// </summary>
    private async Task<WorkItemEntity> ReloadAsync(Guid id)
    {
        await using var db = new FaultInjectingPipelineDbContext(_options);
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    /// <summary>Asserts that <see cref="IKubernetesJobClient.CreateJobAsync"/> was never called.</summary>
    private void VerifyNoJobCreated() =>
        _k8s.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "CreateJobAsync must not be called on any abort path");

    // ── Test 1: kiro agent + empty PVC pool → return before loading WorkItem ──────────────────

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 112–113:
    /// <c>if (isKiroAgent &amp;&amp; claimedPvc is null) return;</c>
    /// and lines 216–219 (<see cref="SelectPvcAsync"/> with an empty pool).
    /// When the PVC pool is empty <c>SelectPvcAsync</c> returns null and the lifecycle
    /// returns immediately — <c>FindAsync</c>, <c>prepareVariant</c>, and K8s are never reached.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_KiroAgentAndEmptyPvcPool_ReturnsBeforeLoadingWorkItem()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(); // empty pool
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(0, "prepareVariant must not be invoked when the PVC pool is empty");
        VerifyNoJobCreated();
        // TODO [WARNING]: pvcs.Should().BeEmpty() is a trivially-passing assertion when the pool starts
        // empty — it contributes no mutation-killing signal of its own. The guard is still pinned by
        // _prepareCalls == 0 and VerifyNoJobCreated(). A stronger signal would require an observable
        // side-effect proving DB was never queried (e.g. a query-count interceptor), but that is out of
        // scope for this issue. Consider adding an EF query interceptor if this class is extended.
        pvcs.Should().BeEmpty("an empty pool has nothing to release");

        var stored = await ReloadAsync(entity.Id);
        stored.K8sJobName.Should().BeNull();
        stored.Status.Should().Be(WorkItemStatus.Pending);
    }

    // ── Test 2: WorkItem not in expected status → release PVC, create no Job ──────────────────

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> line 121
    /// (<c>workItem.Status != ctx.ExpectedInitialStatus</c>) and line 124
    /// (<c>ReleaseClaimedPvc</c>).
    /// A <c>Dispatched</c> item fails the status guard, the PVC is released, and no Job is created.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_WorkItemNotInExpectedStatus_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Dispatched);
        var pvcs = new List<string>(["pvc-0"]);
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(0, "prepareVariant must not be invoked for a stale WorkItem");
        VerifyNoJobCreated();
        pvcs.Should().ContainSingle(pvc => pvc == "pvc-0",
            "the claimed PVC must be returned to the pool");

        var stored = await ReloadAsync(entity.Id);
        stored.K8sJobName.Should().BeNull();
        // TODO [WARNING]: Consider also asserting stored.Status.Should().Be(WorkItemStatus.Dispatched) to
        // independently verify that the abort path did not advance the item's state beyond what was seeded.
        // This would catch a hypothetical mutant where the return; after ReleaseClaimedPvc is deleted but
        // the status guard itself is intact (execution would continue into prepareVariant and beyond).
    }

    // ── Test 3: prepareVariant throws → rethrow and release PVC ──────────────────────────────

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 131–134
    /// (bare <c>catch</c> block in the prepare try): release PVC, re-throw.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantThrows_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(["pvc-0"]);
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            _ => Task.FromException<(bool, Dictionary<string, string>?)>(
                new InvalidOperationException("prepare failed")),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("prepare failed");

        VerifyNoJobCreated();
        pvcs.Should().ContainSingle(pvc => pvc == "pvc-0",
            "the claimed PVC must be returned when prepareVariant throws");

        var stored = await ReloadAsync(entity.Id);
        stored.K8sJobName.Should().BeNull();
    }

    // ── Test 4: prepareVariant returns false → release PVC, create no Job ────────────────────

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 138–140
    /// (<c>if (!shouldProceed)</c> branch) and lines 199–202
    /// (<see cref="ReleaseClaimedPvc"/> body — first test that reaches it with a non-null PVC).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantReturnsFalse_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(["pvc-0"]);
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(shouldContinue: false),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(1, "prepareVariant must be called exactly once");
        VerifyNoJobCreated();
        pvcs.Should().ContainSingle(pvc => pvc == "pvc-0",
            "the claimed PVC must be returned when prepareVariant returns false");

        var stored = await ReloadAsync(entity.Id);
        stored.K8sJobName.Should().BeNull();
    }

    // ── Test 5: DbUpdateConcurrencyException on K8sJobName pre-write → release PVC, no exception ─

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 156–160
    /// (<c>catch (DbUpdateConcurrencyException)</c> on the pre-write):
    /// log, release PVC, return without re-throwing.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnJobNamePreWrite_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(["pvc-0"]);
        using var service = CreateService();
        // FailOnSaveCall = 1: the pre-write is the FIRST SaveChangesAsync call on this context instance.
        // Seeding used a separate instance so its save does not count here.
        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateConcurrencyException("simulated concurrency conflict"),
            FailOnSaveCall = 1
        };

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(shouldContinue: true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().NotThrowAsync();

        VerifyNoJobCreated();
        pvcs.Should().ContainSingle(pvc => pvc == "pvc-0",
            "the claimed PVC must be returned on a pre-write concurrency conflict");

        var stored = await ReloadAsync(entity.Id);
        stored.K8sJobName.Should().BeNull(
            "the K8sJobName assignment was tracked but SaveChanges failed, so the DB must not be updated");
    }

    // ── Test 6: other DbUpdateException on K8sJobName pre-write → release PVC, rethrow ────────

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 162–165
    /// (bare <c>catch</c> on the pre-write): release PVC, re-throw for any
    /// non-concurrency <see cref="DbUpdateException"/>.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_OtherFailureOnJobNamePreWrite_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(["pvc-0"]);
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateException("simulated database failure"),
            FailOnSaveCall = 1
        };

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(shouldContinue: true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().ThrowExactlyAsync<DbUpdateException>()
            .WithMessage("simulated database failure");

        VerifyNoJobCreated();
        pvcs.Should().ContainSingle(pvc => pvc == "pvc-0",
            "the claimed PVC must be returned when the pre-write throws a non-concurrency exception");
    }

    // ── Test 7: DbUpdateConcurrencyException on Dispatched write → swallow, skip success callback ─

    /// <summary>
    /// Pins <c>DispatchLifecycleService.cs</c> lines 265–267, 269
    /// (<c>catch (DbUpdateConcurrencyException)</c> in <c>FinalizeDispatchAsync</c>):
    /// the exception is swallowed, the K8s Job is NOT deleted, and <c>onDispatchSuccess</c>
    /// is never invoked. The stored item stays <c>Pending</c> (the <c>Dispatched</c> save failed).
    /// <para>
    /// PVC note: <c>FinalizeDispatchAsync</c> does NOT call <c>ReleaseClaimedPvc</c> when it
    /// swallows the concurrency exception — the K8s Job was successfully created so the PVC is
    /// legitimately in use. ReconciliationService will reconcile. <c>pvcs</c> must be empty.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnDispatchedWrite_SwallowsConflictAndSkipsSuccessCallback()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>(["pvc-0"]);
        var concurrency = new Dictionary<string, int>();
        var successCalls = 0;

        // Set up K8s mock to let the lifecycle reach FinalizeDispatchAsync.
        _k8s.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _k8s.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "job-uid" } });
        _k8s.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // FailOnSaveCall = 2:
        //   Call 1 — K8sJobName pre-write (line 153) → succeeds
        //   Call 2 — Dispatched status write in FinalizeDispatchAsync (line 258) → throws
        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateConcurrencyException("simulated concurrency conflict"),
            FailOnSaveCall = 2
        };

        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, concurrency),
            Prepare(shouldContinue: true),
            onDispatchSuccess: _ => { successCalls++; return Task.CompletedTask; },
            CancellationToken.None);

        // Act & Assert: no exception escapes
        await act.Should().NotThrowAsync();

        // K8s Job was created; orphan sweep would have deleted it if the race check had fired.
        _k8s.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "CreateJobAsync must have been called exactly once");
        _k8s.Verify(
            k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DeleteJobAsync must not be called — the Job was created successfully before the final save failed");

        // Success callback and concurrency tracking must be skipped (inside the try block that threw).
        successCalls.Should().Be(0, "onDispatchSuccess must not be invoked when the Dispatched save is lost");
        concurrency.Should().BeEmpty("concurrency tracking is inside the try block that threw");

        // PVC stays removed — FinalizeDispatchAsync does not call ReleaseClaimedPvc on the swallow path.
        pvcs.Should().BeEmpty(
            "the PVC must not be returned when the Job was created and the concurrency conflict is swallowed");

        // DB state: K8sJobName was persisted by the pre-write (call 1 succeeded),
        // but Status/ClaimedPvcName were not (call 2 failed).
        var stored = await ReloadAsync(entity.Id);
        stored.Status.Should().Be(WorkItemStatus.Pending,
            "the Dispatched save failed, so the item must still be Pending");
        stored.ClaimedPvcName.Should().BeNull(
            "ClaimedPvcName is written atomically with Status=Dispatched, which was not persisted");
        stored.K8sJobName.Should().Be(
            DispatchLifecycleService.GenerateJobName(entity.Id),
            "K8sJobName was persisted by the pre-write (call 1) before the concurrency conflict on call 2");
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An InMemory <see cref="PipelineDbContext"/> that throws <see cref="SaveException"/>
    /// on call number <see cref="FailOnSaveCall"/> of
    /// <see cref="SaveChangesAsync(CancellationToken)"/>.
    /// All other calls delegate to the base implementation.
    /// The same instance is used for seeding AND dispatching so the InMemory tables are shared;
    /// use a SEPARATE instance for seeding (see <see cref="SeedWorkItemAsync"/>) so that the
    /// seeding save does not count toward <see cref="FailOnSaveCall"/>.
    /// </summary>
    private sealed class FaultInjectingPipelineDbContext : PipelineDbContext
    {
        private int _saveCalls;

        public FaultInjectingPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

        /// <summary>Exception to throw on call number <see cref="FailOnSaveCall"/>; null = never fault.</summary>
        public Exception? SaveException { get; init; }

        /// <summary>1-based index of the <see cref="SaveChangesAsync(CancellationToken)"/> call that faults.</summary>
        public int FailOnSaveCall { get; init; } = 1;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCalls++;
            if (SaveException is not null && _saveCalls == FailOnSaveCall)
                return Task.FromException<int>(SaveException);
            return base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            // Disable concurrency tokens and filtered indexes — not supported by the InMemory provider.
            foreach (var et in mb.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in mb.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    /// <summary>
    /// Factory that always produces a new <see cref="FaultInjectingPipelineDbContext"/>
    /// (with no fault configured — used by <see cref="WorkItemTransitionService"/> for
    /// <c>FailWorkItemAsync</c> calls, which are only reached from paths not exercised here).
    /// </summary>
    // TODO [WARNING]: This factory silently succeeds on any SaveChangesAsync call, including unexpected
    // ones from FailWorkItemAsync if a future refactor routes an abort path through HandleJobCreationFailureAsync.
    // Consider adding a negative assertion in each test (e.g. (await ReloadAsync(entity.Id)).Status
    // .Should().NotBe(WorkItemStatus.Failed)) to make any unexpected FailWorkItemAsync call observable.
    private sealed class FaultInjectingDbContextFactory(DbContextOptions<PipelineDbContext> opts)
        : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new FaultInjectingPipelineDbContext(opts);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<PipelineDbContext>(new FaultInjectingPipelineDbContext(opts));
    }
}
