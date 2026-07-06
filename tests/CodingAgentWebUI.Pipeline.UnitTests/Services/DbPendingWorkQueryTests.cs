using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DbPendingWorkQuery"/> — validates IssueTitle and RepoProviderId
/// extraction from WorkItem Payload JSONB column.
/// </summary>
public sealed class DbPendingWorkQueryTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly DbPendingWorkQuery _sut;

    public DbPendingWorkQueryTests()
    {
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(databaseName: $"DbPendingWorkQueryTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
        {
            ctx.Database.EnsureCreated();
        }

        _dbFactory = new TestDbContextFactory(_dbOptions);
        _sut = new DbPendingWorkQuery(_dbFactory);
    }

    public void Dispose()
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public async Task GetPendingJobsAsync_ExtractsIssueTitleFromPayload()
    {
        // Arrange
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#42",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "user",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 600,
            IssueDetail = new IssueDetail
            {
                Title = "Fix the widget bug",
                Description = "Something is broken",
                Identifier = "owner/repo#42",
                Labels = ["bug"]
            }
        };

        await InsertPendingWorkItem("owner/repo#42", "ip-1", payload);

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].IssueTitle.Should().Be("Fix the widget bug");
    }

    [Fact]
    public async Task GetPendingJobsAsync_ExtractsRepoProviderIdFromPayload()
    {
        // Arrange
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#10",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "repo-provider-xyz",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 300
        };

        await InsertPendingWorkItem("owner/repo#10", "ip-1", payload);

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].RepoProviderId.Should().Be("repo-provider-xyz");
    }

    [Fact]
    public async Task GetPendingJobsAsync_NullPayload_ReturnsEmptyStrings()
    {
        // Arrange — insert without payload
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "owner/repo#99",
                IssueProviderConfigId = "ip-1",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = null,
                AgentSelector = "",
                TaskType = WorkItemTaskType.Implementation
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].IssueTitle.Should().BeEmpty();
        result[0].RepoProviderId.Should().BeEmpty();
    }

    [Fact]
    public void ExtractFromPayload_ValidPayload_ReturnsCorrectValues()
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "x",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp-abc",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Review,
            AgentSelector = "",
            TimeoutSeconds = 60,
            IssueDetail = new IssueDetail
            {
                Title = "My Title",
                Description = "",
                Identifier = "x",
                Labels = []
            }
        };

        var json = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);
        var (title, repoId, _) = DbPendingWorkQuery.ExtractFromPayload(json);

        title.Should().Be("My Title");
        repoId.Should().Be("rp-abc");
    }

    [Fact]
    public void ExtractFromPayload_NullPayload_ReturnsEmptyStrings()
    {
        var (title, repoId, consolidationType) = DbPendingWorkQuery.ExtractFromPayload(null);
        title.Should().BeEmpty();
        repoId.Should().BeEmpty();
        consolidationType.Should().BeNull();
    }

    [Fact]
    public void ExtractFromPayload_InvalidJson_ReturnsEmptyStrings()
    {
        var (title, repoId, consolidationType) = DbPendingWorkQuery.ExtractFromPayload("not valid json{{{");
        title.Should().BeEmpty();
        repoId.Should().BeEmpty();
        consolidationType.Should().BeNull();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private async Task InsertPendingWorkItem(string issueIdentifier, string issueProviderId, JobDistributionRequest payload)
    {
        using var db = new TestPipelineDbContext(_dbOptions);
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = issueIdentifier,
            IssueProviderConfigId = issueProviderId,
            Status = WorkItemStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default),
            AgentSelector = payload.AgentSelector,
            TaskType = payload.TaskType
        });
        await db.SaveChangesAsync();
    }

    // ── Test Infrastructure ─────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
