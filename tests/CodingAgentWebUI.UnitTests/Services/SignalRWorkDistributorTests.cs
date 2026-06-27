using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="SignalRWorkDistributor"/>.
/// Uses in-memory EF Core provider for isolation.
/// </summary>
public sealed class SignalRWorkDistributorTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly Mock<IAgentCommunication> _mockAgentComm;
    private readonly Mock<ISignalRWorkDistributorAgentResolver> _mockResolver;
    private readonly SignalRWorkDistributor _sut;
    private readonly InMemoryDbContextFactory _dbFactory;

    public SignalRWorkDistributorTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"SignalRWorkDistributorTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new InMemoryPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _mockAgentComm = new Mock<IAgentCommunication>();
        _mockResolver = new Mock<ISignalRWorkDistributorAgentResolver>();

        var transitionService = new WorkItemTransitionService(
            _dbFactory,
            NullLogger<WorkItemTransitionService>.Instance);

        _sut = new SignalRWorkDistributor(
            _dbFactory,
            _mockAgentComm.Object,
            transitionService,
            _mockResolver.Object,
            NullLogger<SignalRWorkDistributor>.Instance);
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task DistributeAsync_Success_InsertsWorkItemAndPushesViaSignalR()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveConnectionId(It.IsAny<string>())).Returns("conn-1");
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert
        result.Success.Should().BeTrue();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().BeNull();

        // Verify DB row exists
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Dispatched);
        workItem.IssueIdentifier.Should().Be("owner/repo#1");
        workItem.DispatchedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task DistributeAsync_NoConnectedAgent_MarksWorkItemFailed()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveConnectionId(It.IsAny<string>())).Returns((string?)null);

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("No connected agent");

        // Verify DB row is Failed
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Failed);
        workItem.ErrorMessage.Should().Contain("No connected agent");
    }

    [Fact]
    public async Task DistributeAsync_SignalRThrows_MarksWorkItemFailed()
    {
        // Arrange
        var request = CreateMinimalRequest();
        _mockResolver.Setup(r => r.ResolveConnectionId(It.IsAny<string>())).Returns("conn-1");
        _mockAgentComm
            .Setup(c => c.AssignJobAsync("conn-1", It.IsAny<JobAssignmentMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        // Act
        var result = await _sut.DistributeAsync(request, CancellationToken.None);

        // Assert
        result.Success.Should().BeFalse();
        result.WorkItemId.Should().NotBeNullOrEmpty();
        result.ErrorMessage.Should().Contain("SignalR delivery failed");

        // Verify DB row is Failed
        await using var db = new InMemoryPipelineDbContext(_dbOptions);
        var workItem = await db.WorkItems.FindAsync(Guid.Parse(result.WorkItemId!));
        workItem.Should().NotBeNull();
        workItem!.Status.Should().Be(WorkItemStatus.Failed);
        workItem.FailureReason.Should().Be(FailureReason.InfrastructureFailure);
    }

    [Fact]
    public async Task CancelJobAsync_ExistingDispatchedItem_TransitionsToCancelled()
    {
        // Arrange — insert a Dispatched work item
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                DispatchedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.CancelJobAsync(workItemId.ToString(), CancellationToken.None);

        // Assert
        result.Should().BeTrue();
        await using var dbVerify = new InMemoryPipelineDbContext(_dbOptions);
        var item = await dbVerify.WorkItems.FindAsync(workItemId);
        item!.Status.Should().Be(WorkItemStatus.Cancelled);
        item.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CancelJobAsync_InvalidGuid_ReturnsFalse()
    {
        var result = await _sut.CancelJobAsync("not-a-guid", CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetJobStatusAsync_ExistingItem_ReturnsCorrectStatus()
    {
        // Arrange
        var workItemId = Guid.NewGuid();
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = workItemId,
                IssueIdentifier = "owner/repo#2",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var status = await _sut.GetJobStatusAsync(workItemId.ToString(), CancellationToken.None);

        // Assert
        status.Should().Be(JobDistributionStatus.Running);
    }

    [Fact]
    public async Task GetJobStatusAsync_NonexistentId_ReturnsUnknown()
    {
        var status = await _sut.GetJobStatusAsync(Guid.NewGuid().ToString(), CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task GetJobStatusAsync_InvalidGuid_ReturnsUnknown()
    {
        var status = await _sut.GetJobStatusAsync("not-a-guid", CancellationToken.None);
        status.Should().Be(JobDistributionStatus.Unknown);
    }

    [Fact]
    public async Task IsIssueDistributedAsync_ActiveItem_ReturnsTrue()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#3",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Running,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.IsIssueDistributedAsync("owner/repo#3", "ip-1", CancellationToken.None);

        // Assert
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_TerminalItem_ReturnsFalse()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#4",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Succeeded,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.IsIssueDistributedAsync("owner/repo#4", "ip-1", CancellationToken.None);

        // Assert
        result.Should().BeFalse();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_NoItems_ReturnsFalse()
    {
        var result = await _sut.IsIssueDistributedAsync("nonexistent", "ip-1", CancellationToken.None);
        result.Should().BeFalse();
    }

    [Fact]
    public async Task GetActiveIssueIdentifiersAsync_ReturnsOnlyNonTerminalPairs()
    {
        // Arrange
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.AddRange(
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "active-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Pending,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "active-2",
                    IssueProviderConfigId = "ip-2",
                    Status = WorkItemStatus.Running,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "done-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Succeeded,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "failed-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Failed,
                    AgentSelector = "kiro",
                    CreatedAt = DateTimeOffset.UtcNow,
                    TimeoutSeconds = 300
                });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.GetActiveIssueIdentifiersAsync(CancellationToken.None);

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(("active-1", "ip-1"));
        result.Should().Contain(("active-2", "ip-2"));
        result.Should().NotContain(("done-1", "ip-1"));
        result.Should().NotContain(("failed-1", "ip-1"));
    }

    [Fact]
    public async Task DetectStuckDispatchedItemsAsync_StuckItems_LogsWarningAndReturnsCount()
    {
        // Arrange
        var sixMinutesAgo = DateTimeOffset.UtcNow.AddMinutes(-6);
        var oneMinuteAgo = DateTimeOffset.UtcNow.AddMinutes(-1);

        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.AddRange(
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "stuck-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Dispatched,
                    DispatchedAt = sixMinutesAgo,
                    AgentSelector = "kiro",
                    CreatedAt = sixMinutesAgo,
                    TimeoutSeconds = 300
                },
                new WorkItemEntity
                {
                    Id = Guid.NewGuid(),
                    IssueIdentifier = "recent-1",
                    IssueProviderConfigId = "ip-1",
                    Status = WorkItemStatus.Dispatched,
                    DispatchedAt = oneMinuteAgo,
                    AgentSelector = "kiro",
                    CreatedAt = oneMinuteAgo,
                    TimeoutSeconds = 300
                });
            await db.SaveChangesAsync();
        }

        // Act
        var stuckCount = await _sut.DetectStuckDispatchedItemsAsync(
            TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        stuckCount.Should().Be(1);
    }

    [Fact]
    public async Task DetectStuckDispatchedItemsAsync_NoStuckItems_ReturnsZero()
    {
        // Arrange — only recent items
        await using (var db = new InMemoryPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "recent-1",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Dispatched,
                DispatchedAt = DateTimeOffset.UtcNow,
                AgentSelector = "kiro",
                CreatedAt = DateTimeOffset.UtcNow,
                TimeoutSeconds = 300
            });
            await db.SaveChangesAsync();
        }

        // Act
        var stuckCount = await _sut.DetectStuckDispatchedItemsAsync(
            TimeSpan.FromMinutes(5), CancellationToken.None);

        // Assert
        stuckCount.Should().Be(0);
    }

    private static JobDistributionRequest CreateMinimalRequest() => new()
    {
        IssueIdentifier = "owner/repo#1",
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1",
        InitiatedBy = "pipeline",
        TaskType = WorkItemTaskType.Implementation,
        AgentSelector = "kiro",
        TimeoutSeconds = 300
    };

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options)
            : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var jsonConverter = new ValueConverter<JsonDocument?, string?>(
                v => v == null ? null : v.RootElement.GetRawText(),
                v => v == null ? null : JsonDocument.Parse(v, default));

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                foreach (var property in entityType.GetProperties())
                {
                    if (property.ClrType == typeof(JsonDocument))
                    {
                        property.SetValueConverter(jsonConverter);
                        property.SetColumnType(null);
                    }
                }

                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove partial indexes (not supported by InMemory provider)
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
            => Task.FromResult(CreateDbContext());
    }
}
