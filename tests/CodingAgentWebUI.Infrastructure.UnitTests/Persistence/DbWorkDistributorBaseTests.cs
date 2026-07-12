using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="DbWorkDistributorBase"/> shared behavior.
/// Verifies: RunId resolution (fixes #1154), WorkItem insertion, status queries, and BuildJobAssignmentMessage.
/// </summary>
public class DbWorkDistributorBaseTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly TestDbWorkDistributor _distributor;

    public DbWorkDistributorBaseTests()
    {
        var dbName = $"DbWorkDistributorBaseTests-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        var transitionService = new WorkItemTransitionService(
            _dbFactory, NullLogger<WorkItemTransitionService>.Instance);
        _distributor = new TestDbWorkDistributor(
            _dbFactory, transitionService, NullLogger<TestDbWorkDistributor>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── RunId Resolution (#1154 fix) ────────────────────────────────────

    [Fact]
    public async Task DistributeAsync_WithRunId_UsesPreAssignedId()
    {
        var preAssignedId = Guid.NewGuid().ToString();
        var request = CreateRequest("owner/repo#1", "provider-1") with { RunId = preAssignedId };

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().Be(preAssignedId);

        // Verify DB has the pre-assigned ID
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstOrDefaultAsync(w => w.Id == Guid.Parse(preAssignedId));
        item.Should().NotBeNull();
    }

    [Fact]
    public async Task DistributeAsync_WithoutRunId_GeneratesNewId()
    {
        var request = CreateRequest("owner/repo#2", "provider-2");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        Guid.TryParse(result.WorkItemId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task DistributeAsync_WithInvalidRunId_GeneratesNewId()
    {
        var request = CreateRequest("owner/repo#3", "provider-3") with { RunId = "not-a-guid" };

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBe("not-a-guid");
        Guid.TryParse(result.WorkItemId, out _).Should().BeTrue();
    }

    [Fact]
    public async Task DistributeAsync_WithEmptyRunId_GeneratesNewId()
    {
        var request = CreateRequest("owner/repo#4", "provider-4") with { RunId = "" };

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        Guid.TryParse(result.WorkItemId, out _).Should().BeTrue();
    }

    // ── InsertWorkItemAsync: Status-dependent fields ─────────────────────

    [Fact]
    public async Task InsertWorkItem_PendingStatus_DispatchedAtIsNull()
    {
        var request = CreateRequest("owner/repo#5", "provider-5");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.DispatchedAt.Should().BeNull();
    }

    [Fact]
    public async Task InsertWorkItem_DispatchedStatus_DispatchedAtIsSet()
    {
        var request = CreateRequest("owner/repo#6", "provider-6");

        // Use the Dispatched variant
        var result = await _distributor.DistributeAsDispatched(request, CancellationToken.None);

        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.DispatchedAt.Should().NotBeNull();
        item.Status.Should().Be(WorkItemStatus.Dispatched);
    }

    // ── Shared: CancelJobAsync ──────────────────────────────────────────

    [Fact]
    public async Task CancelJobAsync_ExistingPendingItem_TransitionsToCancelled()
    {
        var request = CreateRequest("owner/repo#7", "provider-7");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        var cancelled = await _distributor.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        cancelled.Should().BeTrue();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var result = await _distributor.CancelJobAsync("invalid-guid", CancellationToken.None);
        result.Should().BeFalse();
    }

    // ── Shared: GetJobStatusAsync ───────────────────────────────────────

    [Fact]
    public async Task GetJobStatusAsync_PendingItem_ReturnsPending()
    {
        var request = CreateRequest("owner/repo#8", "provider-8");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        var status = await _distributor.GetJobStatusAsync(result.WorkItemId!, CancellationToken.None);

        status.Should().Be(JobDistributionStatus.Pending);
    }

    [Fact]
    public async Task GetJobStatusAsync_NonExistentId_ReturnsUnknown()
    {
        var status = await _distributor.GetJobStatusAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    // ── Shared: IsIssueDistributedAsync ─────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_ActiveItem_ReturnsTrue()
    {
        var request = CreateRequest("owner/repo#9", "provider-9");
        await _distributor.DistributeAsync(request, CancellationToken.None);

        var distributed = await _distributor.IsIssueDistributedAsync("owner/repo#9", "provider-9", CancellationToken.None);

        distributed.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_CancelledItem_WithinCooldown_ReturnsTrue()
    {
        // A just-cancelled item is within the restart dedup cooldown window,
        // so IsIssueDistributed returns true to prevent re-dispatch during restart scenarios.
        // After the cooldown expires (5 minutes), it would return false.
        var request = CreateRequest("owner/repo#10", "provider-10");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);
        await _distributor.CancelJobAsync(result.WorkItemId!, CancellationToken.None);

        var distributed = await _distributor.IsIssueDistributedAsync("owner/repo#10", "provider-10", CancellationToken.None);

        distributed.Should().BeTrue("recently-cancelled items within the restart dedup cooldown are treated as distributed");
    }

    // ── Shared: GetActiveIssueIdentifiersAsync ──────────────────────────

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_MixedStatuses_ReturnsOnlyActive()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.AddRange(
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "a-1", IssueProviderConfigId = "p1", Status = WorkItemStatus.Pending, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "a-2", IssueProviderConfigId = "p2", Status = WorkItemStatus.Running, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 },
            new WorkItemEntity { Id = Guid.NewGuid(), IssueIdentifier = "done-1", IssueProviderConfigId = "p3", Status = WorkItemStatus.Succeeded, CreatedAt = DateTimeOffset.UtcNow, AgentSelector = "kiro", TimeoutSeconds = 1800 }
        );
        await db.SaveChangesAsync();

        var active = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        active.Should().HaveCount(2);
        active.Should().Contain(("a-1", "p1"));
        active.Should().Contain(("a-2", "p2"));
        active.Should().NotContain(("done-1", "p3"));
    }

    // ── Restart Dedup: Recently-terminated WorkItems within cooldown ─────
    // TODO: Add boundary condition tests: CompletedAt exactly at cooldown edge (UtcNow.AddMinutes(-5)), terminal item with CompletedAt=null, and Succeeded status within cooldown (see BUG-10 test quality review findings)

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_RecentlyTerminatedWithinCooldown_IncludedInDedup()
    {
        // Simulate: issue with a Failed WorkItem from 1 minute ago (within 5-min cooldown)
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "restart-issue-1",
            IssueProviderConfigId = "provider-restart",
            Status = WorkItemStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1), // Failed 1 min ago — within cooldown
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var active = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        // Should be treated as "active" for dedup purposes — prevents re-dispatch on restart
        active.Should().Contain(("restart-issue-1", "provider-restart"));
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_TerminatedOutsideCooldown_NotIncludedInDedup()
    {
        // Simulate: issue with a Failed WorkItem from 10 minutes ago (outside 5-min cooldown)
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "old-issue-1",
            IssueProviderConfigId = "provider-old",
            Status = WorkItemStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // Failed 10 min ago — outside cooldown
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var active = await _distributor.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        // Should NOT be included — legitimate re-dispatch should be allowed
        active.Should().NotContain(("old-issue-1", "provider-old"));
    }

    [Fact]
    public async Task IsIssueDistributedAsync_RecentlyFailedWithinCooldown_ReturnsTrue()
    {
        // Simulate restart scenario: issue's WorkItem failed 1 minute ago
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "restart-check-1",
            IssueProviderConfigId = "provider-check",
            Status = WorkItemStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-3),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-1), // Failed 1 min ago
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync("restart-check-1", "provider-check", CancellationToken.None);

        distributed.Should().BeTrue("recently-failed WorkItem within cooldown should block re-dispatch");
    }

    [Fact]
    public async Task IsIssueDistributedAsync_FailedOutsideCooldown_ReturnsFalse()
    {
        // Issue failed 10 minutes ago — outside cooldown, eligible for re-dispatch
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "old-check-1",
            IssueProviderConfigId = "provider-old-check",
            Status = WorkItemStatus.Failed,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-15),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-10), // Failed 10 min ago
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync("old-check-1", "provider-old-check", CancellationToken.None);

        distributed.Should().BeFalse("WorkItem failed outside cooldown should allow legitimate re-dispatch");
    }

    [Fact]
    public async Task IsIssueDistributedAsync_CancelledWithinCooldown_ReturnsTrue()
    {
        // Issue cancelled 2 minutes ago (within cooldown)
        await using var db = await _dbFactory.CreateDbContextAsync();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = "cancelled-recent-1",
            IssueProviderConfigId = "provider-cancelled",
            Status = WorkItemStatus.Cancelled,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAt = DateTimeOffset.UtcNow.AddMinutes(-2), // Cancelled 2 min ago
            AgentSelector = "kiro",
            TimeoutSeconds = 1800
        });
        await db.SaveChangesAsync();

        var distributed = await _distributor.IsIssueDistributedAsync("cancelled-recent-1", "provider-cancelled", CancellationToken.None);

        distributed.Should().BeTrue("recently-cancelled WorkItem within cooldown should block re-dispatch");
    }

    // ── OriginalEnqueuedAt: Preserved across re-dispatches ──────────────

    [Fact]
    public async Task InsertWorkItem_FirstDispatch_OriginalEnqueuedAtSetToCreatedAt()
    {
        var request = CreateRequest("fresh-issue-1", "provider-fresh");

        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var item = await db.WorkItems.FirstAsync(w => w.Id == Guid.Parse(result.WorkItemId!));
        item.OriginalEnqueuedAt.Should().NotBeNull();
        // First dispatch: OriginalEnqueuedAt == CreatedAt (within tolerance)
        (item.OriginalEnqueuedAt!.Value - item.CreatedAt).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task InsertWorkItem_ReDispatch_PreservesOriginalEnqueuedAt()
    {
        var originalTime = DateTimeOffset.UtcNow.AddHours(-2);

        // Simulate a prior WorkItem for the same issue (already terminal)
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "redispatch-issue-1",
                IssueProviderConfigId = "provider-redispatch",
                Status = WorkItemStatus.Failed,
                CreatedAt = originalTime,
                CompletedAt = DateTimeOffset.UtcNow.AddHours(-1),
                AgentSelector = "kiro",
                TimeoutSeconds = 1800
            });
            await db.SaveChangesAsync();
        }

        // Now re-dispatch the same issue — OriginalEnqueuedAt should carry forward
        var request = CreateRequest("redispatch-issue-1", "provider-redispatch");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        await using var verifyDb = await _dbFactory.CreateDbContextAsync();
        var newItem = await verifyDb.WorkItems
            .Where(w => w.Id == Guid.Parse(result.WorkItemId!))
            .FirstAsync();

        // OriginalEnqueuedAt should be the original time from the first WorkItem
        newItem.OriginalEnqueuedAt.Should().NotBeNull();
        (newItem.OriginalEnqueuedAt!.Value - originalTime).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
        // CreatedAt should be recent (the new WorkItem's own creation time)
        newItem.CreatedAt.Should().BeAfter(originalTime);
    }

    [Fact]
    public async Task InsertWorkItem_ReDispatch_PreservesEarliestOriginalEnqueuedAt()
    {
        var veryEarlyTime = DateTimeOffset.UtcNow.AddDays(-3);
        var laterTime = DateTimeOffset.UtcNow.AddDays(-1);

        // Simulate two prior WorkItems — one very early (has OriginalEnqueuedAt), one later
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.AddRange(
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "multi-dispatch-1",
                    IssueProviderConfigId = "provider-multi",
                    Status = WorkItemStatus.Failed,
                    CreatedAt = veryEarlyTime,
                    OriginalEnqueuedAt = veryEarlyTime,
                    CompletedAt = veryEarlyTime.AddMinutes(30),
                    AgentSelector = "kiro",
                    TimeoutSeconds = 1800
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "multi-dispatch-1",
                    IssueProviderConfigId = "provider-multi",
                    Status = WorkItemStatus.Failed,
                    CreatedAt = laterTime,
                    OriginalEnqueuedAt = veryEarlyTime, // Carried forward from first
                    CompletedAt = laterTime.AddMinutes(30),
                    AgentSelector = "kiro",
                    TimeoutSeconds = 1800
                }
            );
            await db.SaveChangesAsync();
        }

        // Third dispatch
        var request = CreateRequest("multi-dispatch-1", "provider-multi");
        var result = await _distributor.DistributeAsync(request, CancellationToken.None);

        result.Success.Should().BeTrue();
        await using var verifyDb = await _dbFactory.CreateDbContextAsync();
        var newItem = await verifyDb.WorkItems
            .Where(w => w.Id == Guid.Parse(result.WorkItemId!))
            .FirstAsync();

        // Should preserve the earliest OriginalEnqueuedAt from all prior WorkItems
        (newItem.OriginalEnqueuedAt!.Value - veryEarlyTime).Duration().Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    // ── BuildJobAssignmentMessage ───────────────────────────────────────

    [Fact]
    public void BuildJobAssignmentMessage_MapsAllRequiredFields()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#11", "provider-11") with
        {
            AgentProviderConfigId = "agent-config-1",
            BrainProviderConfigId = "brain-1",
            PipelineProviderConfigId = "pipeline-1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#11", Title = "Test", Description = "Desc", Labels = ["bug"] },
            RunType = PipelineRunType.Review,
            ProjectId = "project-x",
            ProjectName = "My Project"
        };

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(workItemId, request);

        message.JobId.Should().Be(workItemId.ToString());
        message.IssueIdentifier.Should().Be("owner/repo#11");
        message.IssueDetail.Title.Should().Be("Test");
        message.AgentProviderConfigId.Should().Be("agent-config-1");
        message.BrainProviderConfigId.Should().Be("brain-1");
        message.PipelineProviderConfigId.Should().Be("pipeline-1");
        message.RunType.Should().Be(PipelineRunType.Review);
        message.ProjectId.Should().Be("project-x");
        message.ProjectName.Should().Be("My Project");
        message.InitiatedBy.Should().Be("pipeline-loop");
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullOptionals_DefaultsToEmptyCollections()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#12", "provider-12");

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(workItemId, request);

        message.IssueDetail.Should().NotBeNull();
        message.ParsedIssue.Should().NotBeNull();
        message.IssueComments.Should().BeEmpty();
        message.ProviderConfigs.Should().BeEmpty();
        message.QualityGateConfigs.Should().BeEmpty();
        message.McpServers.Should().BeEmpty();
        message.ReviewerConfigs.Should().BeEmpty();
    }

    [Fact]
    public void BuildJobAssignmentMessage_NullAgentProviderConfigId_FallsBackToRepoProviderConfigId()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#13", "provider-13") with { AgentProviderConfigId = null };

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(workItemId, request);

        message.AgentProviderConfigId.Should().Be("repo-provider-1");
    }

    [Fact]
    public void BuildJobAssignmentMessage_MapsConsolidationFields()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("run-123", "consolidation") with
        {
            TaskType = WorkItemTaskType.Consolidation,
            ConsolidationRunType = ConsolidationRunType.RefactoringDetection,
            ConsolidationTemplateId = "template-42",
            ConsolidationWorkspacePath = "/tmp/consolidation/run-123"
        };

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(workItemId, request);

        message.TaskType.Should().Be(WorkItemTaskType.Consolidation);
        message.ConsolidationRunType.Should().Be(ConsolidationRunType.RefactoringDetection);
        message.ConsolidationTemplateId.Should().Be("template-42");
        message.ConsolidationWorkspacePath.Should().Be("/tmp/consolidation/run-123");
    }

    [Fact]
    public void BuildJobAssignmentMessage_NonConsolidation_ConsolidationFieldsAreDefault()
    {
        var workItemId = Guid.NewGuid();
        var request = CreateRequest("owner/repo#14", "provider-14");

        var message = DbWorkDistributorBase.BuildJobAssignmentMessage(workItemId, request);

        message.TaskType.Should().Be(WorkItemTaskType.Implementation);
        message.ConsolidationRunType.Should().BeNull();
        message.ConsolidationTemplateId.Should().BeNull();
        message.ConsolidationWorkspacePath.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest(string issueId, string providerId) => new()
    {
        IssueIdentifier = issueId,
        IssueProviderConfigId = providerId,
        RepoProviderConfigId = "repo-provider-1",
        InitiatedBy = "pipeline-loop",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro,linux",
        TimeoutSeconds = 1800,
        ProjectId = "proj-1",
        RunType = PipelineRunType.Implementation
    };

    // ── Test Infrastructure ─────────────────────────────────────────────

    /// <summary>
    /// Concrete test subclass of DbWorkDistributorBase that exposes insertion with Pending status
    /// (mimicking K8s mode) and a helper to insert as Dispatched (mimicking SignalR mode).
    /// </summary>
    private sealed class TestDbWorkDistributor : DbWorkDistributorBase
    {
        public TestDbWorkDistributor(
            IDbContextFactory<PipelineDbContext> dbFactory,
            WorkItemTransitionService transitionService,
            ILogger logger)
            : base(dbFactory, transitionService, logger) { }

        public override Task<DistributionResult> DistributeAsync(JobDistributionRequest request, CancellationToken ct)
        {
            ArgumentNullException.ThrowIfNull(request);
            return InsertWorkItemAsync(request, WorkItemStatus.Pending, ct, queued: true);
        }

        /// <summary>Helper to test Dispatched-status insertion (SignalR mode behavior).</summary>
        public Task<DistributionResult> DistributeAsDispatched(JobDistributionRequest request, CancellationToken ct)
        {
            return InsertWorkItemAsync(request, WorkItemStatus.Dispatched, ct);
        }
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
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

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;

        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options)
            => _options = options;

        public PipelineDbContext CreateDbContext()
            => new InMemoryPipelineDbContext(_options);

        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult<PipelineDbContext>(new InMemoryPipelineDbContext(_options));
    }
}
