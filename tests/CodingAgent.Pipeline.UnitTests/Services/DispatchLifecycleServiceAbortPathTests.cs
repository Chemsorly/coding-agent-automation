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
/// Unit tests pinning the abort paths of
/// <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>:
/// <list type="bullet">
///   <item>Kiro agent with empty PVC pool — returns without loading the WorkItem</item>
///   <item>WorkItem not in expected status — releases PVC, creates no Job</item>
///   <item><c>prepareVariant</c> throws — rethrows, releases PVC</item>
///   <item><c>prepareVariant</c> returns false — releases PVC, creates no Job</item>
///   <item>Concurrency conflict on K8sJobName pre-write — swallows, releases PVC, creates no Job</item>
///   <item>Other failure on K8sJobName pre-write — rethrows, releases PVC</item>
///   <item>Concurrency conflict on Dispatched write — swallows, skips success callback</item>
/// </list>
/// </summary>
public sealed class DispatchLifecycleServiceAbortPathTests
{
    private readonly DbContextOptions<PipelineDbContext> _options =
        new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"abort-path-test-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private readonly Mock<IKubernetesJobClient> _k8s = new(); // loose: unexpected calls verified, not thrown
    private int _prepareCalls;

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────────

    private DispatchLifecycleService CreateService()
    {
        var factory = new FaultInjectingDbContextFactory(_options);
        var transitionSvc = new WorkItemTransitionService(
            factory,
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

    private DispatchLifecycleContext BuildContext(
        PipelineDbContext db,
        WorkItemEntity entity,
        List<string> availablePvcs,
        Dictionary<string, int> concurrency)
    {
        var projection = new PendingWorkItemProjection
        {
            Id = entity.Id,
            AgentSelector = entity.AgentSelector ?? "kiro,dotnet",
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
            ConcurrencyBySelector: concurrency,
            LogPrefix: "test ");
        // ExpectedInitialStatus defaults to Pending
    }

    private Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>> Prepare(bool shouldContinue)
        => _ =>
        {
            _prepareCalls++;
            return Task.FromResult((shouldContinue, (Dictionary<string, string>?)null));
        };

    private async Task<WorkItemEntity> ReloadAsync(Guid id)
    {
        await using var db = new FaultInjectingPipelineDbContext(_options);
        return await db.WorkItems.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    private void VerifyNoJobCreated()
        => _k8s.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);

    // ── Test 1: kiro agent + empty PVC pool → returns before loading WorkItem ────────────────────

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

        // Assert: no prepare call, no K8s job, pool still empty, item unchanged
        _prepareCalls.Should().Be(0, "prepareVariant must not be called when no PVC is available");
        VerifyNoJobCreated();
        pvcs.Should().BeEmpty("the empty pool must remain empty");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull("K8sJobName must not be written when no PVC is available");
        reloaded.Status.Should().Be(WorkItemStatus.Pending, "status must remain Pending");
    }

    // ── Test 2: WorkItem not in expected status → releases PVC, creates no Job ─────────────────

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_WorkItemNotInExpectedStatus_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange: seed a Dispatched item; ExpectedInitialStatus defaults to Pending, so the check fails
        var entity = await SeedWorkItemAsync(WorkItemStatus.Dispatched);
        var pvcs = new List<string> { "pvc-0" };

        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert: no prepare call, no K8s job, PVC returned, item unchanged
        _prepareCalls.Should().Be(0, "prepareVariant must not be called when the item is not in the expected status");
        VerifyNoJobCreated();
        pvcs.Should().BeEquivalentTo(["pvc-0"], "the claimed PVC must be returned to the pool");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull("K8sJobName must not be written when the status check fails");
    }

    // ── Test 3: prepareVariant throws → rethrows, releases PVC ──────────────────────────────────

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantThrows_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options);

        Func<WorkItemEntity, Task<(bool, Dictionary<string, string>?)>> throwingPrepare =
            _ => Task.FromException<(bool, Dictionary<string, string>?)>(
                new InvalidOperationException("prepare failed"));

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            throwingPrepare,
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().ThrowExactlyAsync<InvalidOperationException>()
            .WithMessage("prepare failed");

        VerifyNoJobCreated();
        pvcs.Should().BeEquivalentTo(["pvc-0"], "the claimed PVC must be returned to the pool when prepareVariant throws");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull("K8sJobName must not be written when prepareVariant throws");
    }

    // ── Test 4: prepareVariant returns false → releases PVC, creates no Job ──────────────────────

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantReturnsFalse_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

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
        pvcs.Should().BeEquivalentTo(["pvc-0"], "the claimed PVC must be returned to the pool when prepareVariant returns false");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull("K8sJobName must not be written when prepareVariant signals abort");
    }

    // ── Test 5: concurrency conflict on K8sJobName pre-write → swallows, releases PVC, no Job ───

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnJobNamePreWrite_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        using var service = CreateService();
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
        await act.Should().NotThrowAsync(
            "DbUpdateConcurrencyException on the K8sJobName pre-write must be swallowed");

        VerifyNoJobCreated();
        pvcs.Should().BeEquivalentTo(["pvc-0"], "the claimed PVC must be returned to the pool on concurrency conflict");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull("K8sJobName must not be persisted when the pre-write fails");
    }

    // ── Test 6: other failure on K8sJobName pre-write → rethrows, releases PVC ──────────────────

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_OtherFailureOnJobNamePreWrite_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

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
        pvcs.Should().BeEquivalentTo(["pvc-0"], "the claimed PVC must be returned to the pool when the pre-write rethrows");
        // TODO: Add reloaded.K8sJobName.Should().BeNull() here to fully verify "the pre-write was
        // aborted at the database level". Without it, this test would still pass if
        // FaultInjectingPipelineDbContext.SaveChangesAsync were changed to call
        // base.SaveChangesAsync(cancellationToken) before throwing (persisting K8sJobName first).
        // Every peer abort-before-commit test (Tests 3, 4, 5) includes this check.
    }

    // ── Test 7: concurrency conflict on Dispatched write → swallows, skips success callback ─────

    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnDispatchedWrite_SwallowsConflictAndSkipsSuccessCallback()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };
        var concurrency = new Dictionary<string, int>();
        var successCalls = 0;

        // Call 1 = K8sJobName pre-write (succeeds); call 2 = Dispatched write in FinalizeDispatchAsync (fails)
        using var service = CreateService();
        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateConcurrencyException("simulated concurrency conflict"),
            FailOnSaveCall = 2
        };

        // Set up Kubernetes mock to simulate a successful Job creation path
        _k8s.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _k8s.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _k8s.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "job-uid" } });

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, concurrency),
            Prepare(shouldContinue: true),
            onDispatchSuccess: _ => { successCalls++; return Task.CompletedTask; },
            CancellationToken.None);

        // Act & Assert
        await act.Should().NotThrowAsync(
            "DbUpdateConcurrencyException on the Dispatched write must be swallowed");

        // Job was created
        _k8s.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "CreateJobAsync must be called exactly once");
        _k8s.Verify(
            k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "DeleteJobAsync must never be called — the job was created successfully");

        // Success callback was NOT invoked (conflict swallowed before it)
        successCalls.Should().Be(0, "onDispatchSuccess must not be called when the Dispatched write conflicts");

        // Concurrency tracking was NOT updated (only happens after successful save)
        concurrency.Should().BeEmpty("ConcurrencyBySelector must not be updated when the Dispatched write conflicts");

        // DB state: the item is still Pending with null ClaimedPvcName (conflict prevented the save)
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.Status.Should().Be(WorkItemStatus.Pending,
            "status must remain Pending when the Dispatched write fails with a concurrency conflict");
        reloaded.ClaimedPvcName.Should().BeNull(
            "ClaimedPvcName must remain null when the Dispatched write fails with a concurrency conflict");
        // K8sJobName was written by the pre-write (call 1 succeeded)
        reloaded.K8sJobName.Should().Be(DispatchLifecycleService.GenerateJobName(entity.Id),
            "K8sJobName must have been persisted by the pre-write (call 1) even when call 2 conflicts");
    }

    // ── Test infrastructure ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// InMemory PipelineDbContext that can fail one SaveChangesAsync call. The same context type
    /// seeds, runs and re-reads, so all three see the same InMemory tables.
    /// </summary>
    private sealed class FaultInjectingPipelineDbContext : PipelineDbContext
    {
        private int _saveCalls;

        public FaultInjectingPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

        /// <summary>Exception returned by call number <see cref="FailOnSaveCall"/>; null never fails.</summary>
        public Exception? SaveException { get; init; }

        /// <summary>1-based number of the SaveChangesAsync call that fails.</summary>
        public int FailOnSaveCall { get; init; } = 1;

        // TODO: Also override the parameterless SaveChangesAsync() overload to route through the
        // fault counter. DbContext.SaveChangesAsync() delegates to SaveChangesAsync(CancellationToken.None)
        // in the base class and bypasses this override. Production code currently always passes a
        // CancellationToken so this is not triggered today, but any future caller using the parameterless
        // overload would silently skip fault injection, causing the affected test to pass even when the
        // guard being tested is broken. Fix: add
        //   public override Task<int> SaveChangesAsync() => SaveChangesAsync(CancellationToken.None);
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            _saveCalls++;
            return SaveException is not null && _saveCalls == FailOnSaveCall
                ? Task.FromException<int>(SaveException)
                : base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            // Disable concurrency tokens and filtered indexes — not supported by in-memory provider.
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

    private sealed class FaultInjectingDbContextFactory(DbContextOptions<PipelineDbContext> opts)
        : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => new FaultInjectingPipelineDbContext(opts);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<PipelineDbContext>(new FaultInjectingPipelineDbContext(opts));
    }
}
