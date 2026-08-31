using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Additional unit tests for WorkItemTransitionService covering branches not exercised by WorkItemTransitionServiceTests:
/// TransitionAsync (item not found, idempotent, invalid transition, concurrency retry, exhausted retries),
/// TransitionIfAsync (item not found, already at target, CAS guard, concurrency retry),
/// GetRetryCountAsync, RequeueAsync, HasAgentErrorSinceAsync, GetLastSuccessfulCompletionAsync,
/// Polly pipeline wiring.
/// </summary>
public class WorkItemTransitionServiceAdditionalTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"WorkItemTransition-Additional-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

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

        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    private static WorkItemTransitionService CreateService(DbContextOptions<PipelineDbContext> opts)
        => new(new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance);

    // ── TransitionAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionAsync_ItemNotFound_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionAsync(Guid.NewGuid(), WorkItemStatus.Dispatched);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_AlreadyAtTarget_ReturnsTrue_Idempotent()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue("idempotent path returns true when already at target");
    }

    [Fact]
    public async Task TransitionAsync_InvalidTransition_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        // Pending → Running is invalid
        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Running);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionAsync_ValidTransition_ChangesStatusAndReturnTrue()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue();

        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task TransitionAsync_WithMutate_SetsAdditionalFields()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var svc = CreateService(opts);
        var completedAt = DateTimeOffset.UtcNow;

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Succeeded, entity =>
        {
            entity.CompletedAt = completedAt;
        });

        result.Should().BeTrue();
        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Succeeded);
        updated.CompletedAt.Should().BeCloseTo(completedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task TransitionAsync_ConcurrencyRetry_SucceedsAfterOneConflict()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        // Factory throws on first save, succeeds on second
        var factory = new ThrowingOnSaveDbContextFactory(opts, throwOnCallNumbers: [1]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue();
    }

    [Fact]
    // TODO: The factory's call counter tracks CreateDbContextAsync invocations, not SaveChangesAsync invocations.
    // This works because TransitionCoreAsync creates a new context per loop iteration (1:1 mapping). If the
    // implementation is ever refactored to reuse a single context across retries, throwOnCallNumbers would shift
    // and the test would silently stop covering the exhausted-retries branch. To make the assumption explicit,
    // expose a CreateCallCount property on ThrowingOnSaveDbContextFactory and add:
    //   factory.CreateCallCount.Should().Be(4); // one context per attempt (attempts 0..3 with maxRetries=3)
    public async Task TransitionAsync_ExhaustedRetries_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        // Factory always throws on save (all 4 calls = attempts 0..3 with maxRetries=3)
        var factory = new ThrowingOnSaveDbContextFactory(opts, throwOnCallNumbers: [1, 2, 3, 4]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched, maxRetries: 3);

        result.Should().BeFalse();
    }

    // ── TransitionIfAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task TransitionIfAsync_ItemNotFound_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.TransitionIfAsync(Guid.NewGuid(), WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionIfAsync_AlreadyAtTarget_ReturnsFalse_NotIdempotent()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Dispatched);
        var svc = CreateService(opts);

        // Item is already Dispatched — TransitionIfAsync is NOT idempotent
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse("TransitionIfAsync fails when already at target (not idempotent)");
    }

    [Fact]
    public async Task TransitionIfAsync_CurrentDoesNotMatchExpected_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Running);
        var svc = CreateService(opts);

        // Expected = Pending but current = Running → CAS fails
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse("CAS guard rejects when current state doesn't match expected");
    }

    [Fact]
    public async Task TransitionIfAsync_InvalidTransition_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        // Pending → Running is not a valid transition
        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Running);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TransitionIfAsync_ValidCAS_TransitionsAndReturnsTrue()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeTrue();

        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    [Fact]
    public async Task TransitionIfAsync_WithMutate_SetsAdditionalFields()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var svc = CreateService(opts);

        var result = await svc.TransitionIfAsync(
            item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched,
            entity => entity.AssignedAgentId = "agent-42");

        result.Should().BeTrue();
        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.AssignedAgentId.Should().Be("agent-42");
    }

    [Fact]
    public async Task TransitionIfAsync_ConcurrencyRetry_SucceedsAfterOneConflict()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var factory = new ThrowingOnSaveDbContextFactory(opts, throwOnCallNumbers: [1]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeTrue();
    }

    [Fact]
    // TODO: Uses the default maxRetries (3) implicitly — throwOnCallNumbers: [1, 2, 3, 4] assumes this default.
    // If the public TransitionIfAsync default ever changes, the throw array would under- or over-cover the retries
    // and the test would pass vacuously. Consider passing maxRetries: 3 explicitly to make the assumption
    // self-documenting and robust against default changes.
    // Same fragile call-count coupling as TransitionAsync_ExhaustedRetries_ReturnsFalse: expose CreateCallCount on
    // ThrowingOnSaveDbContextFactory and assert factory.CreateCallCount.Should().Be(4) to pin iteration count.
    public async Task TransitionIfAsync_AllRetriesExhausted_ReturnsFalse()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);
        var factory = new ThrowingOnSaveDbContextFactory(opts, throwOnCallNumbers: [1, 2, 3, 4]);
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.TransitionIfAsync(item.Id, WorkItemStatus.Pending, WorkItemStatus.Dispatched);

        result.Should().BeFalse();
    }

    // ── GetRetryCountAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCountAsync_ReturnsZero_WhenItemNotFound()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var count = await svc.GetRetryCountAsync(Guid.NewGuid(), CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task GetRetryCountAsync_ReturnsCurrentRetryCount()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts);

        await using var db = new TestPipelineDbContext(opts);
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
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);

        await using var db = new TestPipelineDbContext(opts);
        var entity = await db.WorkItems.FindAsync(item.Id);
        entity!.DispatchedAt = DateTimeOffset.UtcNow;
        entity.AssignedAgentId = "agent-old";
        entity.RetryCount = 1;
        await db.SaveChangesAsync();

        var svc = CreateService(opts);
        await svc.RequeueAsync(item.Id, CancellationToken.None);

        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.Status.Should().Be(WorkItemStatus.Pending);
        updated.RetryCount.Should().Be(2, "RetryCount should be incremented");
        updated.DispatchedAt.Should().BeNull("DispatchedAt should be cleared on requeue");
        updated.AssignedAgentId.Should().BeNull("AssignedAgentId should be cleared on requeue");
    }

    // ── HasAgentErrorSinceAsync ──────────────────────────────────────────────

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenNoMatchingItem()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
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
        var opts = CreateDbOptions();
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

    [Fact]
    public async Task HasAgentErrorSinceAsync_ReturnsFalse_WhenCompletedBeforeSince()
    {
        var opts = CreateDbOptions();
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
        var opts = CreateDbOptions();
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
        var opts = CreateDbOptions();
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
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
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
        var opts = CreateDbOptions();
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
        var opts = CreateDbOptions();
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
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);

        var invoked = false;
        // Build a no-op pipeline using the public API and a separate invocation tracker
        var pipeline = new ResiliencePipelineBuilder().Build(); // passthrough (no-op)

        // Use a provider that sets invoked=true when GetPipeline is called
        var provider = new InvocationTrackingResiliencePipelineProvider(
            WorkItemTransitionService.DbBackgroundPipelineKey, pipeline, () => invoked = true);

        var svc = new WorkItemTransitionService(
            new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance, provider);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeTrue();
        invoked.Should().BeTrue("Polly pipeline provider should have been queried");
    }

    [Fact]
    public void Constructor_WithPollyProvider_KeyNotFound_FallsBackToNoPipeline()
    {
        // When GetPipeline throws for an unknown key, the constructor swallows it and continues
        var opts = CreateDbOptions();
        var provider = new ThrowingResiliencePipelineProvider();

        var act = () => new WorkItemTransitionService(
            new TestDbContextFactory(opts), NullLogger<WorkItemTransitionService>.Instance, provider);

        act.Should().NotThrow("constructor catches the exception and runs without Polly");
    }

    // ── UpdatePriorityWeightAsync ─────────────────────────────────────────────

    [Fact]
    public async Task UpdatePriorityWeightAsync_ItemNotFound_ReturnsNotFound()
    {
        var opts = CreateDbOptions();
        await using var ctx = new TestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(Guid.NewGuid(), 50, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.NotFound);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_ItemNotPending_ReturnsNotPending()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Running);

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(item.Id, 50, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.NotPending);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_PendingItem_UpdatesWeightAndReturnsSuccess()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var svc = CreateService(opts);
        var result = await svc.UpdatePriorityWeightAsync(item.Id, 250, CancellationToken.None);

        result.Should().Be(UpdatePriorityWeightResult.Success);

        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.PriorityWeight.Should().Be(250);
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_AllRetriesExhausted_ReturnsConcurrencyConflict()
    {
        // Arrange: factory throws DbUpdateConcurrencyException on both save calls.
        // maxRetries=1 means loop runs: attempt=0 (throws, retry logged), attempt=1 (throws, returns ConcurrencyConflict).
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var factory = new ThrowingOnSaveDbContextFactory(opts, [1, 2]); // throw on 1st and 2nd save
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.UpdatePriorityWeightAsync(item.Id, 50, CancellationToken.None, maxRetries: 1);

        result.Should().Be(UpdatePriorityWeightResult.ConcurrencyConflict,
            "exhausting retries due to concurrent saves should return ConcurrencyConflict, not NotFound");
    }

    [Fact]
    public async Task UpdatePriorityWeightAsync_FirstAttemptThrows_SecondSucceeds_ReturnsSuccess()
    {
        // Arrange: only the first SaveChanges call throws; the retry succeeds
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, status: WorkItemStatus.Pending);

        var factory = new ThrowingOnSaveDbContextFactory(opts, [1]); // first save throws, second succeeds
        var svc = new WorkItemTransitionService(factory, NullLogger<WorkItemTransitionService>.Instance);

        var result = await svc.UpdatePriorityWeightAsync(item.Id, 75, CancellationToken.None, maxRetries: 3);

        result.Should().Be(UpdatePriorityWeightResult.Success);

        await using var verify = new TestPipelineDbContext(opts);
        var updated = await verify.WorkItems.FindAsync(item.Id);
        updated!.PriorityWeight.Should().Be(75);
    }

    // ── Test Infrastructure ───────────────────────────────────────────────────

    private class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingDbContext : TestPipelineDbContext
    {
        private readonly bool _shouldThrow;
        public ThrowingDbContext(DbContextOptions<PipelineDbContext> opts, bool shouldThrow) : base(opts)
            => _shouldThrow = shouldThrow;
        public override Task<int> SaveChangesAsync(CancellationToken ct = default)
        {
            if (_shouldThrow) throw new DbUpdateConcurrencyException("Simulated conflict");
            return base.SaveChangesAsync(ct);
        }
    }

    private sealed class ThrowingOnSaveDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        private readonly HashSet<int> _throwOnCallNumbers;
        private int _callCount;

        public ThrowingOnSaveDbContextFactory(DbContextOptions<PipelineDbContext> opts, int[] throwOnCallNumbers)
        {
            _opts = opts;
            _throwOnCallNumbers = new HashSet<int>(throwOnCallNumbers);
        }

        public PipelineDbContext CreateDbContext()
        {
            var n = Interlocked.Increment(ref _callCount);
            return new ThrowingDbContext(_opts, _throwOnCallNumbers.Contains(n));
        }
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
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
