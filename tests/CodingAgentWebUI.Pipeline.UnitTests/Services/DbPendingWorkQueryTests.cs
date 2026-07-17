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
    public async Task GetPendingJobsAsync_PopulatesWorkItemId()
    {
        // Arrange
        var expectedId = Guid.NewGuid();
        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = expectedId,
                IssueIdentifier = "owner/repo#77",
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
        result[0].WorkItemId.Should().Be(expectedId.ToString());
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
        var (title, repoId, _, _, _) = DbPendingWorkQuery.ExtractFromPayload(json);

        title.Should().Be("My Title");
        repoId.Should().Be("rp-abc");
    }

    [Fact]
    public async Task GetPendingJobsAsync_ConsolidationTaskType_WithNullPayloadConsolidationRunType_StillMarkedAsConsolidation()
    {
        // Arrange: WorkItem has TaskType=Consolidation but payload does NOT contain ConsolidationRunType.
        // This simulates the scenario where payload extraction fails to parse the consolidation type.
        // IsConsolidation MUST still be true so the UI filters it out of pipeline jobs.
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "consolidation-run-123",
            IssueProviderConfigId = "consolidation",
            RepoProviderConfigId = "",
            InitiatedBy = "consolidation",
            TaskType = WorkItemTaskType.Consolidation,
            AgentSelector = "kiro,dotnet",
            TimeoutSeconds = 1800,
            ConsolidationRunType = null // Simulates missing/unparseable field
        };

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.WorkItems.Add(new WorkItemEntity
            {
                Id = Guid.NewGuid(),
                IssueIdentifier = "consolidation-run-123",
                IssueProviderConfigId = "consolidation",
                Status = WorkItemStatus.Pending,
                CreatedAt = DateTimeOffset.UtcNow,
                Payload = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default),
                AgentSelector = "kiro,dotnet",
                TaskType = WorkItemTaskType.Consolidation
            });
            await db.SaveChangesAsync();
        }

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].IsConsolidation.Should().BeTrue(
            "TaskType=Consolidation is the reliable discriminator; " +
            "ConsolidationRunType from payload is fragile and should not be required");
    }

    [Fact]
    public async Task GetPendingJobsAsync_ExtractsProjectFromPayload()
    {
        // Arrange
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#55",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 600,
            ProjectId = "proj-abc",
            ProjectName = "Default"
        };

        await InsertPendingWorkItem("owner/repo#55", "ip-1", payload);

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Project.Should().NotBeNull();
        result[0].Project!.Id.Should().Be("proj-abc");
        result[0].Project!.Name.Should().Be("Default");
    }

    [Fact]
    public async Task GetPendingJobsAsync_NullProjectInPayload_LeavesProjectNull()
    {
        // Arrange — payload without ProjectId/ProjectName
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#56",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 600
        };

        await InsertPendingWorkItem("owner/repo#56", "ip-1", payload);

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert
        result.Should().HaveCount(1);
        result[0].Project.Should().BeNull();
    }

    [Fact]
    public async Task GetPendingJobsAsync_PartialProjectData_LeavesProjectNull()
    {
        // Arrange — payload with ProjectId set but ProjectName null (partial data)
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "owner/repo#57",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            InitiatedBy = "loop",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 600,
            ProjectId = "proj-orphan",
            ProjectName = null
        };

        await InsertPendingWorkItem("owner/repo#57", "ip-1", payload);

        // Act
        var result = await _sut.GetPendingJobsAsync();

        // Assert — partial project data should not construct a PipelineProject
        result.Should().HaveCount(1);
        result[0].Project.Should().BeNull();
    }

    [Fact]
    public void ExtractFromPayload_WithProject_ReturnsProjectIdAndName()
    {
        var payload = new JobDistributionRequest
        {
            IssueIdentifier = "x",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "",
            TimeoutSeconds = 60,
            ProjectId = "p-123",
            ProjectName = "MyProject"
        };

        var json = JsonSerializer.Serialize(payload, PipelineJsonOptions.Default);
        var (_, _, _, projectId, projectName) = DbPendingWorkQuery.ExtractFromPayload(json);

        projectId.Should().Be("p-123");
        projectName.Should().Be("MyProject");
    }

    [Fact]
    public void ExtractFromPayload_NullPayload_ReturnsEmptyStrings()
    {
        var (title, repoId, consolidationType, projectId, projectName) = DbPendingWorkQuery.ExtractFromPayload(null);
        title.Should().BeEmpty();
        repoId.Should().BeEmpty();
        consolidationType.Should().BeNull();
        projectId.Should().BeNull();
        projectName.Should().BeNull();
    }

    [Fact]
    public void ExtractFromPayload_InvalidJson_ReturnsEmptyStrings()
    {
        var (title, repoId, consolidationType, projectId, projectName) = DbPendingWorkQuery.ExtractFromPayload("not valid json{{{");
        title.Should().BeEmpty();
        repoId.Should().BeEmpty();
        consolidationType.Should().BeNull();
        projectId.Should().BeNull();
        projectName.Should().BeNull();
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
