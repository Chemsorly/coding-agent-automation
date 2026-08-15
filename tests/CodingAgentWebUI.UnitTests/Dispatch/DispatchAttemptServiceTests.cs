using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="DispatchAttemptService"/>.
/// Verifies that <c>TransitionToDispatchedAsync</c> applies the correct entity mutations
/// and that <c>RevertOnFailureAsync</c> delegates faithfully to <see cref="DispatchRevertService"/>
/// with the correct token semantics.
/// Extracted from <see cref="PendingWorkItemDrainService"/> per issue #1914.
/// </summary>
public sealed class DispatchAttemptServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly WorkItemTransitionService _transitionService;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly OrchestratorRunService _runService;

    public DispatchAttemptServiceTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"DispatchAttemptServiceTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    // ── TransitionToDispatchedAsync ──────────────────────────────────────

    [Fact]
    public async Task TransitionToDispatchedAsync_SetsDispatchedAtAndAssignedAgentId()
    {
        // Arrange
        var workItemId = await InsertPendingWorkItem();
        var service = CreateService();

        // Act
        await service.TransitionToDispatchedAsync(workItemId, (AgentId)"agent-42", CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Dispatched);
        item.AssignedAgentId.Should().Be("agent-42");
        item.DispatchedAt.Should().NotBeNull("DispatchedAt must be set by TransitionToDispatchedAsync");
        // TODO: The 5-second tolerance is too wide to catch regressions (e.g. wrong epoch, far-future timestamp).
        // Consider recording a "before" timestamp just before the call and asserting DispatchedAt >= before.
        item.DispatchedAt!.Value.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TransitionToDispatchedAsync_WhenTransitionFails_PropagatesException()
    {
        // Arrange: use a factory that always throws so the transition fails
        var throwingFactory = new AlwaysThrowingDbContextFactory();
        var failingTransition = new WorkItemTransitionService(throwingFactory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, _transitionService, NullLogger<DispatchRevertService>.Instance);
        var service = new DispatchAttemptService(failingTransition, revertHandler);

        // Act & Assert: exception propagates to caller (caller sets dispatchedSuccessfully = false)
        var act = () => service.TransitionToDispatchedAsync(Guid.NewGuid(), (AgentId)"agent-1", CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>("TransitionToDispatchedAsync must propagate DB exceptions");
    }

    // ── RevertOnFailureAsync ─────────────────────────────────────────────

    [Fact]
    public async Task RevertOnFailureAsync_WithIncrementRetryCountTrue_IncrementsRetryCount()
    {
        // Arrange: item starts in Dispatched state (the state it would be in after a successful transition)
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 0);
        var service = CreateService();

        // Act
        await service.RevertOnFailureAsync(workItemId, incrementRetryCount: true, CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending, "item must be reverted to Pending");
        item.RetryCount.Should().Be(1, "incrementRetryCount: true must increment RetryCount");
        item.AssignedAgentId.Should().BeNull("AssignedAgentId must be cleared on revert");
        item.DispatchedAt.Should().BeNull("DispatchedAt must be cleared on revert");
    }

    [Fact]
    public async Task RevertOnFailureAsync_WithIncrementRetryCountFalse_LeavesRetryCountUnchanged()
    {
        // Arrange
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 3);
        var service = CreateService();

        // Act
        await service.RevertOnFailureAsync(workItemId, incrementRetryCount: false, CancellationToken.None);

        // Assert
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Pending);
        item.RetryCount.Should().Be(3, "incrementRetryCount: false must not change RetryCount");
        // TODO: Also assert item.AssignedAgentId is null and item.DispatchedAt is null here,
        // mirroring the assertions in the incrementRetryCount: true test. Without them, a regression
        // that forgets to clear these fields on the false path would go undetected.
    }

    [Fact]
    public async Task RevertOnFailureAsync_WithCancelledToken_DoesNotPropagateException()
    {
        // Arrange: revert with an already-cancelled token — TryRevertToPendingAsync must swallow
        // any OperationCanceledException (stuck-item detector handles unreverted items)
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 0);
        var cancellationAwareFactory = new CancellationAwareDbContextFactory(_dbOptions);
        var cancellingTransition = new WorkItemTransitionService(cancellationAwareFactory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, cancellingTransition, NullLogger<DispatchRevertService>.Instance);
        var service = new DispatchAttemptService(cancellingTransition, revertHandler);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        // Act & Assert: no exception propagates (TryRevertToPendingAsync swallows internally)
        // TODO: This assertion is weak — it verifies no exception propagates, but cannot distinguish
        // "token forwarded and OperationCanceledException swallowed by TryRevertToPendingAsync" from
        // "token not forwarded at all and the revert succeeded silently." A stronger test would use
        // a spy/mock on DispatchRevertService to verify the cancelled token was passed through,
        // or assert the item remains in Dispatched state (proving the revert was skipped).
        var act = () => service.RevertOnFailureAsync(workItemId, incrementRetryCount: false, cts.Token);
        await act.Should().NotThrowAsync("RevertOnFailureAsync must swallow cancellation exceptions");
    }

    [Fact]
    public async Task RevertOnFailureAsync_WhenTransitionThrows_SwallowsException()
    {
        // Arrange: use a factory that always throws to simulate a DB failure during revert
        var workItemId = await InsertDispatchedWorkItem(initialRetryCount: 0);
        var throwingFactory = new AlwaysThrowingDbContextFactory();
        var failingTransition = new WorkItemTransitionService(throwingFactory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, failingTransition, NullLogger<DispatchRevertService>.Instance);
        var service = new DispatchAttemptService(failingTransition, revertHandler);

        // Act & Assert: exception is swallowed (stuck-item detector handles unreverted items)
        var act = () => service.RevertOnFailureAsync(workItemId, incrementRetryCount: false, CancellationToken.None);
        await act.Should().NotThrowAsync("RevertOnFailureAsync must swallow all exceptions from TryRevertToPendingAsync");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private DispatchAttemptService CreateService() =>
        new(_transitionService, new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, _transitionService, NullLogger<DispatchRevertService>.Instance));

    private async Task<Guid> InsertPendingWorkItem()
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"org/repo#{id}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Pending,
            AgentSelector = "",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600,
            Payload = "{}"
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<Guid> InsertDispatchedWorkItem(int initialRetryCount)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            IssueIdentifier = $"org/repo#{id}",
            IssueProviderConfigId = "ip-1",
            Status = WorkItemStatus.Dispatched,
            AgentSelector = "",
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            AssignedAgentId = "agent-1",
            TimeoutSeconds = 3600,
            Payload = "{}",
            RetryCount = initialRetryCount
        });
        await db.SaveChangesAsync();
        return id;
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new PipelineDbContext(_options));
    }

    /// <summary>
    /// A <see cref="IDbContextFactory{PipelineDbContext}"/> that always throws.
    /// Used to force <see cref="WorkItemTransitionService.TransitionAsync"/> to fail.
    /// </summary>
    private sealed class AlwaysThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated DB failure");
    }

    /// <summary>
    /// A <see cref="IDbContextFactory{PipelineDbContext}"/> that throws
    /// <see cref="OperationCanceledException"/> when called with a cancelled token.
    /// Used to verify that <see cref="DispatchAttemptService.RevertOnFailureAsync"/> forwards
    /// the cancellation token to <see cref="DispatchRevertService.TryRevertToPendingAsync"/>.
    /// </summary>
    private sealed class CancellationAwareDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public CancellationAwareDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PipelineDbContext(_options));
        }
    }
}
