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
/// <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>:
/// <list type="bullet">
/// <item><description>Kiro agent with an empty PVC pool returns before loading the WorkItem.</description></item>
/// <item><description>WorkItem not in expected status: releases PVC and creates no Job.</description></item>
/// <item><description><c>prepareVariant</c> throws: rethrows and releases PVC.</description></item>
/// <item><description><c>prepareVariant</c> returns <c>shouldContinue=false</c>: releases PVC and creates no Job.</description></item>
/// <item><description>Concurrency conflict on the <c>K8sJobName</c> pre-write: releases PVC and creates no Job.</description></item>
/// <item><description>Other <c>DbUpdateException</c> on the pre-write: rethrows and releases PVC.</description></item>
/// <item><description>Concurrency conflict on the <c>Dispatched</c> write: swallows and skips success callback.</description></item>
/// </list>
/// </summary>
public sealed class DispatchLifecycleServiceAbortPathTests
{
    private readonly DbContextOptions<PipelineDbContext> _options =
        new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"abort-path-test-{Guid.NewGuid():N}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private readonly Mock<IKubernetesJobClient> _k8s = new();
    private int _prepareCalls;

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────

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
        // TODO [WARNING]: Each seeding context is a fresh instance isolated from the 'db' passed to
        // BuildContext, so the overridden SaveChangesAsync call-counter here is independent.
        // If a future change shares one context instance for both seeding and the lifecycle run,
        // the counter may fire on the wrong call. Keep seeding on a separate context instance.
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

    // ── Test 1: empty PVC pool ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// When the kiro PVC pool is empty, the lifecycle returns immediately before touching the
    /// database — <c>prepareVariant</c> is never invoked and no K8s Job is created.
    /// Pins: <c>if (isKiroAgent &amp;&amp; claimedPvc is null) return;</c> (line 113).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_KiroAgentAndEmptyPvcPool_ReturnsBeforeLoadingWorkItem()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string>();

        await using var db = new FaultInjectingPipelineDbContext(_options);
        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(0, "prepareVariant must not be called when no PVC is available");
        VerifyNoJobCreated();
        pvcs.Should().BeEmpty("an empty pool stays empty — no PVC to return");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull();
        reloaded.Status.Should().Be(WorkItemStatus.Pending);
    }

    // ── Test 2: WorkItem not in expected status ──────────────────────────────────────────────────

    /// <summary>
    /// When the loaded WorkItem is not in the expected status (e.g. already Dispatched by another
    /// process), the lifecycle releases the claimed PVC and returns without creating a Job.
    /// Pins: <c>workItem.Status != ctx.ExpectedInitialStatus</c> guard (line 121) and the
    /// <c>ReleaseClaimedPvc</c> call at line 124.
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_WorkItemNotInExpectedStatus_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange: seed a Dispatched item — ExpectedInitialStatus defaults to Pending
        var entity = await SeedWorkItemAsync(WorkItemStatus.Dispatched);
        var pvcs = new List<string> { "pvc-0" };

        await using var db = new FaultInjectingPipelineDbContext(_options);
        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(0, "prepareVariant must not be called when the WorkItem is stale");
        VerifyNoJobCreated();
        pvcs.Should().Equal(new[] { "pvc-0" }, "the claimed PVC must be returned to the pool");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull();
    }

    // ── Test 3: prepareVariant throws ───────────────────────────────────────────────────────────

    /// <summary>
    /// When <c>prepareVariant</c> throws, the exception propagates to the caller and the claimed
    /// PVC is returned to the available pool.
    /// Pins: <c>catch { ReleaseClaimedPvc(...); throw; }</c> around the prepare call (lines 131-134).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantThrows_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        await using var db = new FaultInjectingPipelineDbContext(_options);
        using var service = CreateService();

        Func<WorkItemEntity, Task<(bool shouldContinue, Dictionary<string, string>? projectSecrets)>> throwingPrepare
            = _ => Task.FromException<(bool, Dictionary<string, string>?)>(
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
        pvcs.Should().Equal(new[] { "pvc-0" }, "the claimed PVC must be returned to the pool on exception");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull();
    }

    // ── Test 4: prepareVariant returns false ─────────────────────────────────────────────────────

    /// <summary>
    /// When <c>prepareVariant</c> returns <c>shouldContinue=false</c>, the lifecycle releases the
    /// claimed PVC and returns without creating a Job.
    /// Pins: <c>if (!shouldProceed) { ReleaseClaimedPvc(...); return; }</c> (lines 138-140).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_PrepareVariantReturnsFalse_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        await using var db = new FaultInjectingPipelineDbContext(_options);
        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(false),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act
        await act();

        // Assert
        _prepareCalls.Should().Be(1);
        VerifyNoJobCreated();
        pvcs.Should().Equal(new[] { "pvc-0" }, "the claimed PVC must be returned to the pool when shouldContinue=false");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull();
    }

    // ── Test 5: concurrency conflict on K8sJobName pre-write ────────────────────────────────────

    /// <summary>
    /// When <c>SaveChangesAsync</c> throws <see cref="DbUpdateConcurrencyException"/> on the
    /// <c>K8sJobName</c> pre-write, the lifecycle swallows the exception, releases the PVC and
    /// returns without creating a Job.
    /// Pins: <c>catch (DbUpdateConcurrencyException) { ReleaseClaimedPvc(...); return; }</c>
    /// (lines 156-160).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnJobNamePreWrite_ReleasesPvcAndCreatesNoJob()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateConcurrencyException("simulated concurrency conflict"),
            FailOnSaveCall = 1
        };
        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().NotThrowAsync();
        VerifyNoJobCreated();
        pvcs.Should().Equal(new[] { "pvc-0" }, "the claimed PVC must be returned to the pool on concurrency conflict");
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.K8sJobName.Should().BeNull();
    }

    // ── Test 6: other DbUpdateException on K8sJobName pre-write ─────────────────────────────────

    /// <summary>
    /// When <c>SaveChangesAsync</c> throws a non-concurrency <see cref="DbUpdateException"/> on the
    /// <c>K8sJobName</c> pre-write, the exception propagates and the PVC is returned to the pool.
    /// Pins: outer <c>catch { ReleaseClaimedPvc(...); throw; }</c> for the pre-write (lines 162-165).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_OtherFailureOnJobNamePreWrite_RethrowsAndReleasesPvc()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };

        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateException("simulated database failure"),
            FailOnSaveCall = 1
        };
        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, new Dictionary<string, int>()),
            Prepare(true),
            onDispatchSuccess: null,
            CancellationToken.None);

        // Act & Assert
        await act.Should().ThrowExactlyAsync<DbUpdateException>()
            .WithMessage("simulated database failure");
        VerifyNoJobCreated();
        pvcs.Should().Equal(new[] { "pvc-0" }, "the claimed PVC must be returned to the pool on database failure");
    }

    // ── Test 7: concurrency conflict on Dispatched write ────────────────────────────────────────

    /// <summary>
    /// When <c>FinalizeDispatchAsync</c> catches a <see cref="DbUpdateConcurrencyException"/> on
    /// the <c>Dispatched</c> write, it swallows the exception and skips the success callback.
    /// The K8s Job was already created — the Job exists in K8s; ReconciliationService reconciles.
    /// Pins: <c>catch (DbUpdateConcurrencyException)</c> in <c>FinalizeDispatchAsync</c> (lines 265-269).
    /// </summary>
    [Fact]
    public async Task ExecuteDispatchLifecycleAsync_ConcurrencyConflictOnDispatchedWrite_SwallowsConflictAndSkipsSuccessCallback()
    {
        // Arrange
        var entity = await SeedWorkItemAsync(WorkItemStatus.Pending);
        var pvcs = new List<string> { "pvc-0" };
        var concurrency = new Dictionary<string, int>();
        var successCalls = 0;

        // call 1 = K8sJobName pre-write (must succeed); call 2 = Dispatched write (must fail)
        await using var db = new FaultInjectingPipelineDbContext(_options)
        {
            SaveException = new DbUpdateConcurrencyException("simulated concurrency conflict"),
            FailOnSaveCall = 2
        };

        _k8s.Setup(k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _k8s.Setup(k => k.ReadJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new V1Job { Metadata = new V1ObjectMeta { Name = "caa-test", Uid = "job-uid" } });
        _k8s.Setup(k => k.CreateSecretAsync(It.IsAny<V1Secret>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        using var service = CreateService();

        Func<Task> act = () => service.ExecuteDispatchLifecycleAsync(
            BuildContext(db, entity, pvcs, concurrency),
            Prepare(true),
            onDispatchSuccess: _ => { successCalls++; return Task.CompletedTask; },
            CancellationToken.None);

        // Act & Assert: no exception escapes
        await act.Should().NotThrowAsync();

        // CreateJobAsync called once; DeleteJobAsync never called
        _k8s.Verify(
            k => k.CreateJobAsync(It.IsAny<V1Job>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the K8s Job must have been created before the concurrency conflict");
        _k8s.Verify(
            k => k.DeleteJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "a concurrency conflict on the Dispatched write must not delete the Job");

        // Success callback was not invoked
        successCalls.Should().Be(0, "onDispatchSuccess must not be invoked when the Dispatched write fails");

        // ConcurrencyBySelector remains empty (incremented only after a successful Dispatched save)
        concurrency.Should().BeEmpty("concurrency tracking must not be updated when the Dispatched write fails");

        // The database row is still Pending with no ClaimedPvcName
        // (the concurrency conflict means our Dispatched write did not land)
        var reloaded = await ReloadAsync(entity.Id);
        reloaded.Status.Should().Be(WorkItemStatus.Pending,
            "the database row must still be Pending because the Dispatched write was lost");
        reloaded.ClaimedPvcName.Should().BeNull(
            "ClaimedPvcName must not be persisted when the Dispatched write failed");

        // K8sJobName was pre-written successfully (call 1 succeeded)
        reloaded.K8sJobName.Should().Be(
            DispatchLifecycleService.GenerateJobName(entity.Id),
            "K8sJobName must be set from the successful pre-write");

        // TODO [WARNING]: pvcs was ["pvc-0"] before the run; SelectPvcAsync removes it on claim
        // and FinalizeDispatchAsync does NOT return it on a Dispatched-write concurrency conflict
        // (the Job already exists in K8s at that point). An assertion
        // pvcs.Should().BeEmpty("PVC is consumed by the successful pre-write path and not returned")
        // would pin this intentional behaviour and guard against an accidental ReleaseClaimedPvc
        // being added to the FinalizeDispatchAsync catch block in the future.
    }

    // ── Test infrastructure ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// InMemory <see cref="PipelineDbContext"/> that can fail one <c>SaveChangesAsync</c> call.
    /// The same context type is used for seeding, running and re-reading so all three operations
    /// see the same InMemory tables.
    /// </summary>
    private sealed class FaultInjectingPipelineDbContext : PipelineDbContext
    {
        private int _saveCalls;

        public FaultInjectingPipelineDbContext(DbContextOptions<PipelineDbContext> opts) : base(opts) { }

        /// <summary>Exception returned by call number <see cref="FailOnSaveCall"/>; null never fails.</summary>
        public Exception? SaveException { get; init; }

        /// <summary>1-based number of the <c>SaveChangesAsync</c> call that fails.</summary>
        public int FailOnSaveCall { get; init; } = 1;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            // TODO [WARNING]: _saveCalls is a plain int incremented with ++. EF Core executes saves
            // sequentially within a single DbContext instance so there is no concurrent-increment risk
            // in current tests. If a future test shares one context instance across concurrent saves
            // (e.g. via Task.WhenAll), replace with Interlocked.Increment(ref _saveCalls) to avoid
            // delivering the fault to the wrong call.
            _saveCalls++;
            return SaveException is not null && _saveCalls == FailOnSaveCall
                ? Task.FromException<int>(SaveException)
                : base.SaveChangesAsync(cancellationToken);
        }

        protected override void OnModelCreating(ModelBuilder mb)
        {
            base.OnModelCreating(mb);
            // Disable concurrency tokens and filtered indexes — not supported by the in-memory provider.
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
