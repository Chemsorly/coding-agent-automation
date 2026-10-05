using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Unit tests for WorkItemTransitionService.IsValidTransition (pure state machine logic)
/// and TryRecoverFromInfrastructureFailureAsync concurrency retry behavior.
/// TransitionAsync integration behavior is validated by Property 1 (task 3.2) against real Postgres.
/// </summary>
public class WorkItemTransitionServiceTests
{
    [Theory]
    // Pending→Dispatched: used by ClaimWorkItem (POST /api/work-items/{id}/claim) for all task types.
    // Dispatched→Pending: used by RequeueWorkItem (POST /api/work-items/{id}/requeue) when K8s Job
    // creation fails after a successful claim — item must return to Pending for retry.
    // The consolidation-specific TODO comments on these transitions are removed in #2566;
    // the transitions themselves remain valid for the general dispatch and requeue paths.
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

    [Fact]
    public void PendingToDispatched_IsValid_ForClaimWorkItemEndpoint()
    {
        // Pending→Dispatched is used by ClaimWorkItem (POST /api/work-items/{id}/claim),
        // which the Scheduler's WorkItemDispatchLoop calls for all task types.
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Pending, WorkItemStatus.Dispatched)
            .Should().BeTrue("Pending→Dispatched is used by ClaimWorkItem for all task types via the Scheduler");
    }

    [Fact]
    public void DispatchedToPending_IsValid_ForRequeueWorkItemEndpoint()
    {
        // Dispatched→Pending is used by RequeueWorkItem (POST /api/work-items/{id}/requeue)
        // when K8s Job creation fails after a successful claim — the item must be returned
        // to the Pending queue for retry rather than being stuck in Dispatched state.
        // The consolidation-specific TODO comments on this transition are removed in #2566;
        // the transition itself remains valid for the general requeue path.
        WorkItemTransitionService.IsValidTransition(WorkItemStatus.Dispatched, WorkItemStatus.Pending)
            .Should().BeTrue("Dispatched→Pending is used by RequeueWorkItem when K8s Job creation fails after claim");
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

    // ── TransitionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_ItemNotFound_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionAsync(Guid.NewGuid(), WorkItemStatus.Dispatched);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_AlreadyAtTarget_ReturnsTrue_Idempotent()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue("idempotent path returns true when already at target");
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        // Pending → Running is invalid
        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Running);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_ValidTransition_ChangesStatusAndReturnTrue()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Failed→Pending (valid recovery path) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);
        var svc = CreateService(opts);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Pending);

        result.Should().BeTrue();

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task TransitionAsync_WithMutate_SetsAdditionalFields()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var svc = CreateService(opts);
        var completedAt = DateTimeOffset.UtcNow;

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Succeeded, entity =>
        {
            entity.CompletedAt = completedAt;
        });

        result.Should().BeTrue();
        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Succeeded);
        updated.CompletedAt.Should().BeCloseTo(completedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TransitionAsync_ConcurrencyRetry_SucceedsAfterOneConflict()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        // Factory throws on first save, succeeds on second
        var factory = new ConcurrencyConflictDbContextFactory(opts, throwOnSaveCallNumbers: [1]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Running);

        result.Should().BeTrue();
    }

    [Fact]
    // TODO: The factory's call counter tracks CreateDbContextAsync invocations, not SaveChangesAsync invocations.
    // This works because TransitionCoreAsync creates a new context per loop iteration (1:1 mapping). If the
    // implementation is ever refactored to reuse a single context across retries, throwOnCallNumbers would shift
    // and the test would silently stop covering the exhausted-retries branch. To make the assumption explicit,
    // expose a CreateCallCount property on ConcurrencyConflictDbContextFactory and add:
    //   factory.CreateCallCount.Should().Be(4); // one context per attempt (attempts 0..3 with maxRetries=3)
    public async Task TransitionAsync_ExhaustedRetries_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        // Factory always throws on save (all 4 calls = attempts 0..3 with maxRetries=3)
        var factory = new ConcurrencyConflictDbContextFactory(opts, throwOnSaveCallNumbers: [1, 2, 3, 4]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Running, maxRetries: 3);

        result.Should().BeFalse();
    }

    // ── TransitionIfAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionIfAsync_ItemNotFound_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionIfAsync(Guid.NewGuid(), WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionIfAsync_AlreadyAtTarget_ReturnsFalse_NotIdempotent()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        // Item is already Dispatched — TransitionIfAsync is NOT idempotent
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse("TransitionIfAsync fails when already at target (not idempotent)");
    }

    [Fact]
    public async Task TransitionIfAsync_CurrentDoesNotMatchExpected_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var svc = CreateService(opts);

        // Expected = Pending but current = Running → CAS fails
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse("CAS guard rejects when current state doesn't match expected");
    }

    [Fact]
    public async Task TransitionIfAsync_InvalidTransition_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        // Pending → Running is not a valid transition
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Running);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionIfAsync_ValidCAS_TransitionsAndReturnsTrue()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Dispatched, WorkItemStatus.Running);

        result.Should().BeTrue();

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task TransitionIfAsync_WithMutate_SetsAdditionalFields()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionIfAsync(
            item.Id, WorkItemStatus.Dispatched, WorkItemStatus.Running,
            entity => entity.AssignedAgentId = "agent-42");

        result.Should().BeTrue();
        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.AssignedAgentId.Should().Be("agent-42");
    }

    [Fact]
    public async Task TransitionIfAsync_ConcurrencyRetry_SucceedsAfterOneConflict()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var factory = new ConcurrencyConflictDbContextFactory(opts, throwOnSaveCallNumbers: [1]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Dispatched, WorkItemStatus.Running);

        result.Should().BeTrue();
    }

    [Fact]
    // TODO: Uses the default maxRetries (3) implicitly — throwOnSaveCallNumbers: [1, 2, 3, 4] assumes this default.
    // If the public TransitionIfAsync default ever changes, the throw array would under- or over-cover the retries
    // and the test would pass vacuously. Consider passing maxRetries: 3 explicitly to make the assumption
    // self-documenting and robust against default changes.
    // Same fragile call-count coupling as TransitionAsync_ExhaustedRetries_ReturnsFalse: expose CreateCallCount on
    // ConcurrencyConflictDbContextFactory and assert factory.CreateCallCount.Should().Be(4) to pin iteration count.
    public async Task TransitionIfAsync_AllRetriesExhausted_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Dispatched→Running (valid operational transition) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var factory = new ConcurrencyConflictDbContextFactory(opts, throwOnSaveCallNumbers: [1, 2, 3, 4]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Dispatched, WorkItemStatus.Running);

        result.Should().BeFalse();
    }

    // ── TransitionDetailedAsync ──────────────────────────────────────────────

    [Fact]
    public async Task TransitionDetailedAsync_ItemNotFound_ReturnsNotFound()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionDetailedAsync(Guid.NewGuid(), WorkItemStatus.Dispatched);

        result.Should().Be(TransitionResult.NotFound);
    }

    [Fact]
    public async Task TransitionDetailedAsync_AlreadyAtTarget_ReturnsAlreadyAtTarget()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionDetailedAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().Be(TransitionResult.AlreadyAtTarget,
            "item is already at the target status — should be an idempotent no-op");
    }

    [Fact]
    public async Task TransitionDetailedAsync_InvalidTransition_ReturnsRejected()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        // TODO: Pending → Running is used here as an invalid transition fixture. If the state
        // machine is ever extended to allow that pair directly, this test will silently exercise
        // a valid-transition path and likely return TransitionResult.Transitioned instead of
        // Rejected — causing a test failure for the wrong reason. Consider using a structurally
        // impossible transition (e.g. a terminal status → any non-terminal status, such as
        // Failed → Running) which cannot become valid regardless of future state-machine changes.
        // Also consider adding a DB-state re-read assertion to verify the item's status is still
        // Pending after the rejected call (guards against silent partial mutations).
        // Pending → Running is not a valid transition
        var result = await svc.TransitionDetailedAsync(item.Id, WorkItemStatus.Running);

        result.Should().Be(TransitionResult.Rejected);
    }

    [Fact]
    public async Task TransitionDetailedAsync_ValidTransition_ReturnsTransitioned()
    {
        var opts = CreateInMemoryDbOptions();
        // Use Failed→Pending (valid recovery path) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);
        var svc = CreateService(opts);

        var result = await svc.TransitionDetailedAsync(item.Id, WorkItemStatus.Pending);

        result.Should().Be(TransitionResult.Transitioned);

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
    }

    [Fact]
    public async Task TransitionDetailedAsync_ValidTransition_InvokesMutate()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var svc = CreateService(opts);
        var completedAt = DateTimeOffset.UtcNow;

        var result = await svc.TransitionDetailedAsync(item.Id, WorkItemStatus.Succeeded,
            mutate: entity => entity.CompletedAt = completedAt);

        result.Should().Be(TransitionResult.Transitioned);
        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.CompletedAt.Should().BeCloseTo(completedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TransitionDetailedAsync_AlreadyAtTarget_DoesNotInvokeMutate()
    {
        // The mutate callback must NOT be invoked for idempotent no-ops.
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            completedAt: DateTimeOffset.UtcNow.AddMinutes(-5));
        var svc = CreateService(opts);

        var mutateCalled = false;
        var result = await svc.TransitionDetailedAsync(item.Id, WorkItemStatus.Succeeded,
            mutate: _ => mutateCalled = true);

        result.Should().Be(TransitionResult.AlreadyAtTarget);
        mutateCalled.Should().BeFalse("mutate must not be called on the idempotent no-op path");
    }

    // Bool-overload backward-compatibility: AlreadyAtTarget maps to true
    [Fact]
    public async Task TransitionAsync_Bool_AlreadyAtTarget_StillReturnsTrue()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue("the bool overload must preserve the idempotent-true contract");
    }

    // Bool-overload backward-compatibility: NotFound maps to false
    [Fact]
    public async Task TransitionAsync_Bool_NotFound_ReturnsFalse()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionAsync(Guid.NewGuid(), WorkItemStatus.Dispatched);

        result.Should().BeFalse("the bool overload must map NotFound to false");
    }

    // ── GetRetryCountAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCountAsync_ReturnsZero_WhenItemNotFound()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var count = await svc.GetRetryCountAsync(Guid.NewGuid(), CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetRetryCountAsync_ReturnsCurrentRetryCount()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts);

        await using var db = new InMemoryPipelineDbContext(opts);
        var entity = await db.WorkItems.FindAsync(item.Id);
        entity!.RetryCount = 3;
        await db.SaveChangesAsync();

        var svc = CreateService(opts);
        var count = await svc.GetRetryCountAsync(item.Id, CancellationToken.None);
        count.Should().Be(3);
    }

    // ── RequeueAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RequeueAsync_IncrementsRetryCountAndClearsDispatchFields()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);

        await using var db = new InMemoryPipelineDbContext(opts);
        var entity = await db.WorkItems.FindAsync(item.Id);
        entity!.DispatchedAt = DateTimeOffset.UtcNow;
        entity.AssignedAgentId = "agent-old";
        entity.ClaimedPvcName = "kiro-creds-pvc-1";
        entity.RetryCount = 1;
        await db.SaveChangesAsync();

        var svc = CreateService(opts);
        await svc.RequeueAsync(item.Id, CancellationToken.None);

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.RetryCount.Should().Be(2, "RetryCount should be incremented");
        updated.DispatchedAt.Should().BeNull("DispatchedAt should be cleared on requeue");
        updated.AssignedAgentId.Should().BeNull("AssignedAgentId should be cleared on requeue");
        updated.ClaimedPvcName.Should().BeNull("ClaimedPvcName should be cleared on requeue so the credential slot is not held indefinitely");
    }

    // ── HasAgentErrorSinceAsync ──────────────────────────────────────────────

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenNoMatchingItem()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#1", (ProviderConfigId)"ip-1",
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenFailureReasonIsNotAgentError()
    {
        var opts = CreateInMemoryDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            failureReason: FailureReason.InfrastructureFailure,
            issueIdentifier: "org/repo#10", providerConfigId: "ip-1",
            completedAt: DateTimeOffset.UtcNow);
        var svc = CreateService(opts);

        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#10", (ProviderConfigId)"ip-1",
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        result.Should().BeFalse("InfrastructureFailure must not match AgentError filter");
    }

    /// <summary>
    /// Issue #2956: GateRejected failures must NOT trigger HasAgentErrorSince.
    /// Gate rejections are expected outcomes, not agent errors — re-dispatch must not
    /// force a fresh analysis unnecessarily.
    /// </summary>
    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenFailureReasonIsGateRejected()
    {
        var opts = CreateInMemoryDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            failureReason: FailureReason.GateRejected,
            issueIdentifier: "org/repo#10g", providerConfigId: "ip-g",
            completedAt: DateTimeOffset.UtcNow);
        var svc = CreateService(opts);

        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#10g", (ProviderConfigId)"ip-g",
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        result.Should().BeFalse("GateRejected must not match AgentError filter — gate decisions are expected outcomes, not agent failures");
    }

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenCompletedBeforeSince()
    {
        var opts = CreateInMemoryDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            failureReason: FailureReason.AgentError,
            issueIdentifier: "org/repo#11", providerConfigId: "ip-1",
            completedAt: DateTimeOffset.UtcNow.AddDays(-2));
        var svc = CreateService(opts);

        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#11", (ProviderConfigId)"ip-1",
            DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        result.Should().BeFalse("completion was before the since cutoff");
    }

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsTrue_WhenMatchingAgentErrorAfterSince()
    {
        var opts = CreateInMemoryDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            failureReason: FailureReason.AgentError,
            issueIdentifier: "org/repo#12", providerConfigId: "ip-2",
            completedAt: DateTimeOffset.UtcNow);
        var svc = CreateService(opts);

        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#12", (ProviderConfigId)"ip-2",
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenStatusNotFailed()
    {
        var opts = CreateInMemoryDbOptions();
        // Succeeded but with AgentError failure reason (shouldn't happen in practice — query requires Failed)
        await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            failureReason: FailureReason.AgentError,
            issueIdentifier: "org/repo#13", providerConfigId: "ip-1",
            completedAt: DateTimeOffset.UtcNow);
        var svc = CreateService(opts);

        var result = await svc.HasAgentErrorSinceAsync(
            (IssueIdentifier)"org/repo#13", (ProviderConfigId)"ip-1",
            DateTimeOffset.UtcNow.AddHours(-1), CancellationToken.None);

        result.Should().BeFalse("query requires Status == Failed");
    }

    // ── GetLastSuccessfulCompletionAsync ──────────────────────────────────────

    [Fact]
    public async Task GetLastSuccessfulCompletionAsync_ReturnsNull_WhenNoSuccessForIssue()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.GetLastSuccessfulCompletionAsync(
            (IssueIdentifier)"org/repo#20", (ProviderConfigId)"ip-1",
            CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetLastSuccessfulCompletionAsync_ReturnsLatestSuccessCompletedAt()
    {
        var opts = CreateInMemoryDbOptions();
        var earlier = DateTimeOffset.UtcNow.AddDays(-5);
        var latest = DateTimeOffset.UtcNow.AddDays(-1);

        await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            issueIdentifier: "org/repo#21", providerConfigId: "ip-1", completedAt: earlier);
        await SeedWorkItemAsync(opts, WorkItemStatus.Succeeded,
            issueIdentifier: "org/repo#21", providerConfigId: "ip-1", completedAt: latest);

        var svc = CreateService(opts);
        var result = await svc.GetLastSuccessfulCompletionAsync(
            (IssueIdentifier)"org/repo#21", (ProviderConfigId)"ip-1",
            CancellationToken.None);

        result.Should().NotBeNull();
        result!.Value.Should().BeCloseTo(latest, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetLastSuccessfulCompletionAsync_IgnoresFailedItems()
    {
        var opts = CreateInMemoryDbOptions();
        await SeedWorkItemAsync(opts, WorkItemStatus.Failed,
            issueIdentifier: "org/repo#22", providerConfigId: "ip-1",
            completedAt: DateTimeOffset.UtcNow);

        var svc = CreateService(opts);
        var result = await svc.GetLastSuccessfulCompletionAsync(
            (IssueIdentifier)"org/repo#22", (ProviderConfigId)"ip-1",
            CancellationToken.None);

        result.Should().BeNull("failed items must not count as successful completions");
    }

    // ── Polly pipeline wiring ─────────────────────────────────────────────────

    [Fact]
    public async Task Constructor_WithPollyProvider_UsesPipelineForTransitionAsync()
    {
        // Verify that when a ResiliencePipelineProvider is supplied, the service still succeeds —
        // proving the Polly execution wrapper does not break the normal path.
        var opts = CreateInMemoryDbOptions();
        // Use Failed→Pending (valid recovery path) instead of removed Pending→Dispatched
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);

        var invoked = false;
        // Build a no-op pipeline using the public API and a separate invocation tracker
        var pipeline = new ResiliencePipelineBuilder().Build(); // passthrough (no-op)

        // Use a provider that sets invoked=true when GetPipeline is called
        var provider = new InvocationTrackingResiliencePipelineProvider(
            WorkItemTransitionService.DbBackgroundPipelineKey, pipeline, () => invoked = true);

        var svc = new WorkItemTransitionService(
            new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance, provider);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Pending);

        result.Should().BeTrue();
        invoked.Should().BeTrue("Polly pipeline provider should have been queried");
    }

    [Fact]
    public void Constructor_WithPollyProvider_KeyNotFound_FallsBackToNoPipeline()
    {
        // When GetPipeline throws for an unknown key, the constructor swallows it and continues
        var opts = CreateInMemoryDbOptions();
        var provider = new ThrowingResiliencePipelineProvider();

        var act = () => new WorkItemTransitionService(
            new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance, provider);

        act.Should().NotThrow("constructor catches the exception and runs without Polly");
    }

    // ── UpdatePriorityWeightAsync ─────────────────────────────────────────────

    [Fact]
    public async Task UpdatePriorityWeightAsync_ItemNotFound_ReturnsNotFound()
    {
        var opts = CreateInMemoryDbOptions();
        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(Guid.NewGuid(), 50, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.NotFound);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_ItemNotPending_ReturnsNotPending()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Running);

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(item.Id, 50, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.NotPending);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_PendingItem_UpdatesWeightAndReturnsSuccess()
    {
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(item.Id, 250, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.Success);

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.PriorityWeight.Should().Be(250);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_AllRetriesExhausted_ReturnsConcurrencyConflict()
    {
        // Arrange: factory throws DbUpdateConcurrencyException on both save calls.
        // maxRetries=1 means loop runs: attempt=0 (throws, retry logged), attempt=1 (throws, returns ConcurrencyConflict).
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var factory = new ConcurrencyConflictDbContextFactory(opts, [1, 2]); // throw on 1st and 2nd save
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.UpdatePriorityWeightAsync(item.Id, 50, CancellationToken.None, maxRetries: 1);

        result.Should().Be(UpdatePriorityWeightResult.ConcurrencyConflict,
            "exhausting retries due to concurrent saves should return ConcurrencyConflict, not NotFound");
        // TODO: [WARNING] No assertion verifies that PriorityWeight was NOT updated in the database
        // after retry exhaustion. A scenario where the update was partially applied before the exception
        // was swallowed would go undetected. Add: `verify.WorkItems.Find(item.Id).PriorityWeight.Should().Be(0)`
        // (the seeded default) to confirm the column remains unchanged after exhaustion.
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_FirstAttemptThrows_SecondSucceeds_ReturnsSuccess()
    {
        // Arrange: only the first SaveChanges call throws; the retry succeeds
        var opts = CreateInMemoryDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var factory = new ConcurrencyConflictDbContextFactory(opts, [1]); // first save throws, second succeeds
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.UpdatePriorityWeightAsync(item.Id, 75, CancellationToken.None, maxRetries: 3);

        result.Should().Be(UpdatePriorityWeightResult.Success);

        await using var verify = new InMemoryPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.PriorityWeight.Should().Be(75);
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

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status = WorkItemStatus.Pending,
        FailureReason? failureReason = null,
        string issueIdentifier = "org/repo#1",
        string providerConfigId = "ip-1",
        DateTimeOffset? completedAt = null)
    {
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = providerConfigId,
            Status = status,
            FailureReason = failureReason,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            CompletedAt = completedAt
        };

        await using var ctx = new InMemoryPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemTransitionService CreateService(DbContextOptions<PipelineDbContext> opts)
        => new(new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance);
    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class FakeResiliencePipelineProvider : ResiliencePipelineProvider<string>
    {
        private readonly string _key;
        private readonly ResiliencePipeline _pipeline;
        public FakeResiliencePipelineProvider(string key, ResiliencePipeline pipeline)
        {
            _key = key;
            _pipeline = pipeline;
        }
        public override ResiliencePipeline<T> GetPipeline<T>(string key) => throw new NotSupportedException();
        public override ResiliencePipeline GetPipeline(string key)
        {
            if (key != _key) throw new KeyNotFoundException(key);
            return _pipeline;
        }
#pragma warning disable CS8765 // Nullability of type of parameter
        public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline)
        {
            if (key == _key) { pipeline = _pipeline; return true; }
            pipeline = null!; return false;
        }
        public override bool TryGetPipeline<T>(string key, out ResiliencePipeline<T> pipeline)
        {
            pipeline = null!; return false;
        }
#pragma warning restore CS8765
    }

    private sealed class ThrowingResiliencePipelineProvider : ResiliencePipelineProvider<string>
    {
        public override ResiliencePipeline<T> GetPipeline<T>(string key) => throw new KeyNotFoundException(key);
        public override ResiliencePipeline GetPipeline(string key) => throw new KeyNotFoundException(key);
#pragma warning disable CS8765
        public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline) { pipeline = null!; return false; }
        public override bool TryGetPipeline<T>(string key, out ResiliencePipeline<T> pipeline) { pipeline = null!; return false; }
#pragma warning restore CS8765
    }

    private sealed class InvocationTrackingResiliencePipelineProvider : ResiliencePipelineProvider<string>
    {
        private readonly string _key;
        private readonly ResiliencePipeline _pipeline;
        private readonly Action _onGetPipeline;

        public InvocationTrackingResiliencePipelineProvider(string key, ResiliencePipeline pipeline, Action onGetPipeline)
        {
            _key = key;
            _pipeline = pipeline;
            _onGetPipeline = onGetPipeline;
        }

        public override ResiliencePipeline<T> GetPipeline<T>(string key) => throw new NotSupportedException();
        public override ResiliencePipeline GetPipeline(string key)
        {
            if (key != _key) throw new KeyNotFoundException(key);
            _onGetPipeline();
            return _pipeline;
        }
#pragma warning disable CS8765
        public override bool TryGetPipeline(string key, out ResiliencePipeline pipeline)
        {
            if (key == _key) { pipeline = _pipeline; return true; }
            pipeline = null!; return false;
        }
        public override bool TryGetPipeline<T>(string key, out ResiliencePipeline<T> pipeline)
        {
            pipeline = null!; return false;
        }
#pragma warning restore CS8765
    }
}
