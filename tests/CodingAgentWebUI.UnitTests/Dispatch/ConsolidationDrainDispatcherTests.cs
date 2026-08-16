using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Text.Json;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ConsolidationDrainDispatcher.TryDispatchAsync"/>.
/// Tests call the public interface method directly — no reflection needed.
/// Ported from <c>DispatchConsolidationItemAsyncTests</c> (which used reflection on
/// <c>PendingWorkItemDrainService.DispatchConsolidationItemAsync</c>)
/// as part of issue #2063 (extraction into dedicated class).
/// </summary>
public sealed class ConsolidationDrainDispatcherTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver = new();
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<IConsolidationDispatchService> _mockConsolidationDispatchService = new();
    private readonly Mock<IConsolidationRunStore> _mockConsolidationRunStore = new();
    private readonly OrchestratorRunService _runService;
    private readonly WorkItemTransitionService _transitionService;

    public ConsolidationDrainDispatcherTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"ConsolidationDrainDispatcherTest_{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _runService = new OrchestratorRunService(Serilog.Log.Logger);
        _transitionService = new WorkItemTransitionService(_dbFactory, NullLogger<WorkItemTransitionService>.Instance);
    }

    // ── Happy path ────────────────────────────────────────────────────────

    [Fact]
    public async Task TryDispatchAsync_SuccessfulDispatch_ReturnsTrue_AndAssignsJob()
    {
        // TODO: workItemId == Guid.Parse(runId) couples the DB entity's primary key to the run ID string,
        // which means the TryDispatchToAgentAsync mock matches for both the correct runId and any
        // Guid-derived variant. Use a distinct runId and workItemId in the fixture to make argument
        // matching genuinely discriminating — a mismatch would silently return false rather than throw.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _mockResolver.Setup(r => r.AssignJob("agent-1", workItemId.ToString()));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeTrue("successful dispatch must return true");
        _mockResolver.Verify(r => r.AssignJob("agent-1", workItemId.ToString()), Times.Once);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Dispatched);
        stored.AssignedAgentId.Should().Be("agent-1");
    }

    // ── Cancelled/failed run ──────────────────────────────────────────────

    [Fact]
    public async Task TryDispatchAsync_CancelledRun_TransitionsToCancelled_ReturnsFalse()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Cancelled, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("cancelled run must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Cancelled);
        stored.CompletedAt.Should().NotBeNull();
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryDispatchAsync_FailedRun_TransitionsToCancelled_ReturnsFalse()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Failed, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("failed run must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Cancelled);
        stored.CompletedAt.Should().NotBeNull();
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task TryDispatchAsync_NullRun_TransitionsToCancelled_ReturnsFalse()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("null run must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Cancelled);
    }

    // ── Dispatch failure (false return) ───────────────────────────────────

    [Fact]
    public async Task TryDispatchAsync_DispatchReturnsFalse_RevertsToPending_RetryCountUnchanged()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("failed dispatch must return false");
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1", It.IsAny<CancellationToken>()),
            Times.Once);
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.AssignedAgentId.Should().BeNull();
        stored.DispatchedAt.Should().BeNull();
        stored.RetryCount.Should().Be(0, "consolidation false-path must NOT increment RetryCount");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
    }

    // ── Exception during dispatch ─────────────────────────────────────────

    [Fact]
    public async Task TryDispatchAsync_DispatchThrowsException_RevertsToPending_RetryCountUnchanged()
    {
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Token vending failed"));
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        var dispatcher = CreateDispatcher();

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("exception during dispatch must return false");
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
        stored.AssignedAgentId.Should().BeNull();
        stored.DispatchedAt.Should().BeNull();
        stored.RetryCount.Should().Be(0, "consolidation exception-path must NOT increment RetryCount");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
    }

    // ── Exception on Dispatched transition (before dispatch) ─────────────

    [Fact]
    public async Task TryDispatchAsync_TransitionToDispatchedThrows_AgentReleased_RevertCalledWithNone_ReturnsFalse()
    {
        // TODO: AlwaysThrowingDbContextFactory is used for both DispatchAttemptService and transitionService,
        // so the cancel-run-guard path (_transitionService.TransitionAsync for Cancelled) also throws —
        // not just the Dispatched transition. This means the test does not distinguish between
        // "item stayed Pending because revert succeeded" and "item stayed Pending because it was never
        // mutated". A scenario where the code accidentally skips the revert entirely would still pass.
        // Consider splitting into two tests: one where only the Dispatched transition throws (using a
        // factory that throws only on the second call), to verify the revert path is actually exercised.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, "tpl-1", "/tmp/ws", "agent-1");

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Force the Dispatched transition to throw by using a factory that always throws
        var throwingFactory = new AlwaysThrowingDbContextFactory();
        var throwingTransition = new WorkItemTransitionService(throwingFactory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, _transitionService,
            NullLogger<DispatchRevertService>.Instance);
        var dispatchAttemptService = new DispatchAttemptService(throwingTransition, revertHandler);
        var dispatcher = new ConsolidationDrainDispatcher(
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object,
            dispatchAttemptService,
            throwingTransition,
            _mockResolver.Object,
            revertHandler,
            NullLogger<ConsolidationDrainDispatcher>.Instance);

        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", CancellationToken.None);

        result.Should().BeFalse("transition failure must return false");
        _mockResolver.Verify(r => r.ReleaseAgent("agent-1"), Times.Once);
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(It.IsAny<string>(), It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<CancellationToken>()),
            Times.Never, "TryDispatchToAgentAsync must not be called when the Dispatched transition fails");
        // Item stays Pending (revert is idempotent: Pending→Pending is a no-op)
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Pending);
    }

    // ── False-return ct-forwarding ────────────────────────────────────────

    [Fact]
    public async Task TryDispatchAsync_DispatchReturnsFalse_UsesCallerCancellationToken()
    {
        // TODO: The assertion (stored.Status == Dispatched) relies on CancellationAwareDbContextFactory
        // throwing OperationCanceledException from CreateDbContextAsync when the token is already
        // cancelled at call time. This is correct while WorkItemTransitionService calls
        // CreateDbContextAsync before any token check. If TransitionAsync is ever refactored to check
        // the token before calling the factory, the cancellation propagation chain may change and this
        // test's expected outcome may no longer hold. The test is not definitively racy (the token is
        // cancelled synchronously inside the mock callback), but its correctness depends on the
        // implementation detail of when TransitionAsync checks ct vs. when it calls the factory.
        //
        // Verifies that when dispatch returns false, the revert path forwards the caller's ct
        // to TryRevertToPendingAsync rather than ignoring it.
        //
        // Strategy: cancel the CTS inside the TryDispatchToAgentAsync mock callback. At that point
        // the initial TransitionAsync(Dispatched) has already completed successfully, so the item
        // is in Dispatched state. The false-return path then calls RevertOnFailureAsync(ct) with
        // the now-cancelled token, which causes CancellationAwareDbContextFactory to throw
        // OperationCanceledException. RevertOnFailureAsync swallows the exception, leaving the
        // item Dispatched. If ct were not forwarded (CancellationToken.None used instead), the revert
        // transition would succeed and the item would be Pending — failing this assertion.
        var runId = Guid.NewGuid().ToString();
        var workItemId = Guid.Parse(runId);
        var (item, request) = await InsertAndBuildItem(workItemId, runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", "agent-1");

        using var cts = new CancellationTokenSource();

        _mockConsolidationRunStore.Setup(s => s.GetByIdAsync(runId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationRun { RunId = runId, Status = ConsolidationRunStatus.Queued, Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow });
        _mockConsolidationDispatchService
            .Setup(d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", (AgentId)"agent-1", It.IsAny<CancellationToken>()))
            .Returns((string _, ConsolidationRunType _, TemplateId? _, string _, AgentId _, CancellationToken _) =>
            {
                cts.Cancel(); // cancel AFTER the initial Dispatched transition succeeded
                return Task.FromResult(false);
            });
        _mockResolver.Setup(r => r.ReleaseAgent("agent-1"));

        // Use a CancellationAwareDbContextFactory so that when the cancelled token reaches
        // RevertOnFailureAsync, the revert transition fails
        var cancellationAwareFactory = new CancellationAwareDbContextFactory(_dbOptions);
        var cancellingTransitionService = new WorkItemTransitionService(cancellationAwareFactory, NullLogger<WorkItemTransitionService>.Instance);
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, cancellingTransitionService,
            NullLogger<DispatchRevertService>.Instance);
        var dispatchAttemptService = new DispatchAttemptService(cancellingTransitionService, revertHandler);
        var dispatcher = new ConsolidationDrainDispatcher(
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object,
            dispatchAttemptService,
            cancellingTransitionService,
            _mockResolver.Object,
            revertHandler,
            NullLogger<ConsolidationDrainDispatcher>.Instance);

        // Act: pass cts.Token — still live when the method starts, cancelled inside the mock callback
        var result = await dispatcher.TryDispatchAsync(item, request, (AgentId)"agent-1", cts.Token);

        result.Should().BeFalse("failed dispatch must return false");
        _mockConsolidationDispatchService.Verify(
            d => d.TryDispatchToAgentAsync(runId, ConsolidationRunType.BrainConsolidation, null, "/tmp/ws", (AgentId)"agent-1", It.IsAny<CancellationToken>()),
            Times.Once,
            "TryDispatchToAgentAsync must be called — confirming the dispatch path was exercised");

        // The revert was cancelled via the forwarded ct → item remains Dispatched
        // If ct were ignored and CancellationToken.None used instead, the revert would succeed → Pending
        await using var db = await _dbFactory.CreateDbContextAsync();
        var stored = await db.WorkItems.FindAsync(workItemId);
        stored!.Status.Should().Be(WorkItemStatus.Dispatched,
            "revert was cancelled via the caller's ct — item must remain Dispatched, confirming ct was forwarded");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private ConsolidationDrainDispatcher CreateDispatcher()
    {
        var revertHandler = new DispatchRevertService(
            _dbFactory, _mockResolver.Object, _runService, _transitionService,
            NullLogger<DispatchRevertService>.Instance);
        var dispatchAttemptService = new DispatchAttemptService(_transitionService, revertHandler);
        return new ConsolidationDrainDispatcher(
            _mockConsolidationDispatchService.Object,
            _mockConsolidationRunStore.Object,
            dispatchAttemptService,
            _transitionService,
            _mockResolver.Object,
            revertHandler,
            NullLogger<ConsolidationDrainDispatcher>.Instance);
    }

    private async Task<(WorkItemEntity item, JobDistributionRequest request)> InsertAndBuildItem(
        Guid workItemId, string runId, ConsolidationRunType runType, string? templateId,
        string workspacePath, string agentSelector)
    {
        var request = new JobDistributionRequest
        {
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = agentSelector,
            TimeoutSeconds = 0,
            ConsolidationRunType = runType,
            ConsolidationTemplateId = templateId,
            ConsolidationWorkspacePath = workspacePath,
            RunId = runId
        };
        var payload = JsonSerializer.Serialize(request, PipelineJsonOptions.Default);

        var entity = new WorkItemEntity
        {
            Id = workItemId,
            TaskType = WorkItemTaskType.Consolidation,
            IssueIdentifier = runId,
            IssueProviderConfigId = "consolidation",
            Status = WorkItemStatus.Pending,
            Payload = payload,
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 0
        };
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(entity);
        await db.SaveChangesAsync();

        return (entity, request);
    }

    public void Dispose()
    {
        using var db = new PipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
        GC.SuppressFinalize(this);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(new PipelineDbContext(_options));
    }

    private sealed class AlwaysThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => throw new InvalidOperationException("Simulated DB failure");
    }

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
