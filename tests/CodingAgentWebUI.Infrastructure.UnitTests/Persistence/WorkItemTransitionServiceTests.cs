using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for WorkItemTransitionService.IsValidTransition (pure state machine logic)
/// and TryRecoverFromInfrastructureFailureAsync concurrency retry behavior.
/// TransitionAsync integration behavior is validated by Property 1 (task 3.2) against real Postgres.
/// </summary>
public class WorkItemTransitionServiceTests
{
    [Theory]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Dispatched, true)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Running, false)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Pending, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Running, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Succeeded, false)]
    [InlineData(WorkItemStatus.Dispatched, WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Succeeded, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Failed, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Cancelled, true)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Pending, false)]
    [InlineData(WorkItemStatus.Running, WorkItemStatus.Dispatched, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Failed, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Cancelled, false)]
    [InlineData(WorkItemStatus.Succeeded, WorkItemStatus.Pending, false)]
    // Requeue paths added by Req 6.1 (POST /api/work-items/{id}/requeue): Failed/Cancelled → Pending
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Failed, WorkItemStatus.Running, false)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Pending, true)]
    [InlineData(WorkItemStatus.Cancelled, WorkItemStatus.Running, false)]
    public void IsValidTransition_ReturnsExpected(WorkItemStatus current, WorkItemStatus target, bool expected)
    {
        WorkItemTransitionService.IsValidTransition(current, target).Should().Be(expected);
    }

    [Fact]
    public void IsValidTransition_TerminalStates_CannotTransitionAnywhere()
    {
        // Succeeded is truly terminal — no outgoing transitions.
        // Failed and Cancelled can requeue to Pending (Req 6.1), so they are not fully terminal.
        var trulyTerminal = new[] { WorkItemStatus.Succeeded };
        var allStatuses = Enum.GetValues<WorkItemStatus>();

        foreach (var terminal in trulyTerminal)
        foreach (var target in allStatuses)
        {
            WorkItemTransitionService.IsValidTransition(terminal, target).Should().BeFalse(
                $"Terminal state {terminal} should not transition to {target}");
        }
    }

    [Fact]
    public void IsValidTransition_SameState_ReturnsFalse()
    {
        // Same state is not a "valid transition" — it's handled by idempotency check in TransitionAsync
        var allStatuses = Enum.GetValues<WorkItemStatus>();
        foreach (var status in allStatuses)
        {
            WorkItemTransitionService.IsValidTransition(status, status).Should().BeFalse(
                $"Same-state {status} → {status} should return false (idempotency handled separately)");
        }
    }

    // ── TryRecoverFromInfrastructureFailureAsync Concurrency Retry Tests ─────

    // TODO: Add test coverage for the Polly resilience pipeline wrapping code path (lines 156-161 of
    // WorkItemTransitionService.cs). All current tests construct the service without a resilience pipeline,
    // so the _resiliencePipeline.ExecuteAsync branch is never exercised. Use a test double that tracks
    // execution to verify the wiring is correct.

    [Fact]
    public async Task TryRecoverFromInfrastructureFailure_ConcurrencyConflict_RetriesAndSucceeds()
    {
        // Arrange: WorkItem in Failed/InfrastructureFailure state
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#100",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.InfrastructureFailure,
                ErrorMessage = "SignalR delivery failure: timeout",
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Factory that throws DbUpdateConcurrencyException on the first SaveChangesAsync, then succeeds
        var factory = new ConcurrencyConflictDbContextFactory(dbOptions, throwOnSaveCallNumbers: [1]);
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act
        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert: Retried after concurrency conflict and succeeded
        result.Should().BeTrue();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
        }
    }

    [Fact]
    // TODO: This test asserts result.Should().BeFalse() but does not explicitly verify the method does not throw.
    // A defensive addition of `await act.Should().NotThrowAsync()` before the value check would make an accidental
    // exception propagation immediately visible rather than potentially swallowed by the async test context.
    // Also consider adding a read-back assertion (item.Status.Should().Be(WorkItemStatus.Failed)) to confirm no
    // partial mutation occurred on intermediate retry attempts.
    public async Task TryRecoverFromInfrastructureFailure_WhenAllRetriesExhaustWithConcurrencyException_ReturnsFalse()
    {
        // Arrange: WorkItem in Failed/InfrastructureFailure state
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#101",
                IssueProviderConfigId = "ip-2",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.InfrastructureFailure,
                ErrorMessage = "SignalR delivery failure: timeout",
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Factory that throws on ALL save attempts (calls 1,2,3,4 = attempts 0,1,2,3)
        var factory = new ConcurrencyConflictDbContextFactory(dbOptions, throwOnSaveCallNumbers: [1, 2, 3, 4]);
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act — no exception should escape the method boundary
        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert: returns false without throwing — method must never propagate DbUpdateConcurrencyException
        result.Should().BeFalse();
    }

    [Fact]
    // TODO: This test validates the final outcome (returns false) but does not uniquely prove a retry
    // occurred. Assert that the factory's CreateDbContextAsync was called exactly 2 times to confirm
    // the retry loop was exercised after the concurrency conflict.
    public async Task TryRecoverFromInfrastructureFailure_ConcurrencyConflict_StateChangedByOtherWriter_ReturnsFalse()
    {
        // Arrange: WorkItem in Failed/InfrastructureFailure state
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#102",
                IssueProviderConfigId = "ip-3",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.InfrastructureFailure,
                ErrorMessage = "SignalR delivery failure: timeout",
                CreatedAt = DateTimeOffset.UtcNow,
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Factory that throws on first save AND simulates another writer changing state to Succeeded
        // at SaveChangesAsync time (not at context creation time), so the retry re-reads the
        // modified entity and observes that recovery preconditions no longer hold.
        var factory = new ConcurrencyConflictDbContextFactory(
            dbOptions,
            throwOnSaveCallNumbers: [1],
            modifyEntityAfterThrow: async (opts) =>
            {
                await using var db = new InMemoryPipelineDbContext(opts);
                var item = await db.WorkItems.FindAsync(workItemId);
                if (item is not null)
                {
                    item.Status = WorkItemStatus.Succeeded;
                    item.CompletedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync();
                }
            });
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act: First attempt reads Failed entity, tries to save, gets concurrency exception
        // (side-effect changes entity to Succeeded). Retry re-reads entity, sees Status=Succeeded,
        // and returns false because recovery precondition (Status == Failed) no longer holds.
        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Running);

        // Assert: Returns false (item is now Succeeded, not Failed — recovery precondition no longer holds)
        result.Should().BeFalse();
    }

    // ── TryRecoverFromInfrastructureFailureAsync — Timeout reason (Issue #2146) ─

    [Fact]
    public async Task TryRecoverFromInfrastructureFailure_WithTimeoutReason_Succeeds()
    {
        // Arrange: WorkItem in Failed/Timeout state (set by ReconciliationLoop.EnforceTimeoutsAsync)
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#200",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.Timeout,
                ErrorMessage = "Job timed out after 3600s",
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var factory = new ConcurrencyConflictDbContextFactory(dbOptions, throwOnSaveCallNumbers: []);
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act
        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert
        result.Should().BeTrue("Failed/Timeout should be recoverable");

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Succeeded);
            item.CompletedAt.Should().NotBeNull("BuildMutationAction must set CompletedAt on recovery");
        }
    }

    [Fact]
    public async Task TryRecoverFromInfrastructureFailure_WithAgentErrorReason_ReturnsFalse()
    {
        // Arrange: WorkItem in Failed/AgentError state — must NOT be recoverable
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#201",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.AgentError,
                ErrorMessage = "Agent reported an error",
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var factory = new ConcurrencyConflictDbContextFactory(dbOptions, throwOnSaveCallNumbers: []);
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        // Act
        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        // Assert
        result.Should().BeFalse("AgentError must not be recoverable");

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.FailureReason.Should().Be(FailureReason.AgentError);
        }
    }

    [Fact]
    public async Task TryRecoverFromInfrastructureFailure_WithQualityGateExhaustedReason_ReturnsFalse()
    {
        // Acceptance criteria: QualityGateExhausted failures must NOT be recoverable.
        // Negative test at the WorkItemTransitionService layer to catch guard regressions
        // (e.g., accidentally including QualityGateExhausted in the allowlist) before
        // they propagate to higher layers.
        var workItemId = Guid.NewGuid();
        var dbOptions = CreateInMemoryDbOptions();

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            db.Database.EnsureCreated();
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#202",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Failed,
                FailureReason = FailureReason.QualityGateExhausted,
                ErrorMessage = "Quality gate retries exhausted",
                CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
                CreatedAt = DateTimeOffset.UtcNow.AddHours(-1),
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        var factory = new ConcurrencyConflictDbContextFactory(dbOptions, throwOnSaveCallNumbers: []);
        var service = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await service.TryRecoverFromInfrastructureFailureAsync(
            workItemId, WorkItemStatus.Succeeded);

        result.Should().BeFalse("QualityGateExhausted must not be recoverable");

        await using (var db = new InMemoryPipelineDbContext(dbOptions))
        {
            var item = await db.WorkItems.FindAsync(workItemId);
            item!.Status.Should().Be(WorkItemStatus.Failed);
            item.FailureReason.Should().Be(FailureReason.QualityGateExhausted);
        }
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateInMemoryDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"WorkItemTransitionTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                {
                    entityType.RemoveIndex(index);
                }
            }
        }
    }

    /// <summary>
    /// A PipelineDbContext subclass that throws DbUpdateConcurrencyException from SaveChangesAsync
    /// when instructed to by the factory.
    /// Optionally runs a side-effect (simulating another writer) just before throwing.
    /// </summary>
    private sealed class ThrowingPipelineDbContext : InMemoryPipelineDbContext
    {
        private readonly bool _shouldThrow;
        private readonly Func<Task>? _onThrowSideEffect;

        public ThrowingPipelineDbContext(
            DbContextOptions<PipelineDbContext> options,
            bool shouldThrow,
            Func<Task>? onThrowSideEffect = null)
            : base(options)
        {
            _shouldThrow = shouldThrow;
            _onThrowSideEffect = onThrowSideEffect;
        }

        public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (_shouldThrow)
            {
                if (_onThrowSideEffect is not null)
                    await _onThrowSideEffect();
                throw new DbUpdateConcurrencyException("Simulated concurrency conflict");
            }

            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Factory that returns ThrowingPipelineDbContext for specific call numbers,
    /// and normal InMemoryPipelineDbContext otherwise. Tracks CreateDbContextAsync call count.
    /// Optionally passes a side-effect (simulating another writer) to the throwing context,
    /// which executes the side-effect at SaveChangesAsync time (just before throwing).
    /// </summary>
    private sealed class ConcurrencyConflictDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        private readonly HashSet<int> _throwOnSaveCallNumbers;
        private readonly Func<DbContextOptions<PipelineDbContext>, Task>? _modifyEntityAfterThrow;
        private int _createCallCount;
        private bool _sideEffectExecuted;

        public ConcurrencyConflictDbContextFactory(
            DbContextOptions<PipelineDbContext> options,
            int[] throwOnSaveCallNumbers,
            Func<DbContextOptions<PipelineDbContext>, Task>? modifyEntityAfterThrow = null)
        {
            _options = options;
            _throwOnSaveCallNumbers = new HashSet<int>(throwOnSaveCallNumbers);
            _modifyEntityAfterThrow = modifyEntityAfterThrow;
        }

        public PipelineDbContext CreateDbContext()
        {
            var callNumber = Interlocked.Increment(ref _createCallCount);
            bool shouldThrow = _throwOnSaveCallNumbers.Contains(callNumber);

            Func<Task>? sideEffect = null;
            if (shouldThrow && _modifyEntityAfterThrow is not null && !_sideEffectExecuted)
            {
                _sideEffectExecuted = true;
                var opts = _options;
                var modifier = _modifyEntityAfterThrow;
                sideEffect = () => modifier(opts);
            }

            return new ThrowingPipelineDbContext(_options, shouldThrow, sideEffect);
        }

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
