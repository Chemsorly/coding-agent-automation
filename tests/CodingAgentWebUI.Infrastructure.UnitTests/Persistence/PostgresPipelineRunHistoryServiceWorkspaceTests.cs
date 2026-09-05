using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="PostgresPipelineRunHistoryService.CleanupExpiredWorkspaces"/>.
/// Uses temp directories for filesystem assertions and InMemory EF for DB-backed tests.
/// Note: TryDeleteWorkspace tests were consolidated into WorkspaceDeletionGuardTests.
/// </summary>
public sealed class PostgresPipelineRunHistoryServiceWorkspaceTests : IDisposable
{
    private readonly string _tempBase;
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly Mock<ILogger> _mockLogger;
    private readonly PostgresPipelineRunHistoryService _sut;

    public PostgresPipelineRunHistoryServiceWorkspaceTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), $"ws-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempBase);

        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"WorkspaceTests-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _mockLogger = new Mock<ILogger>();
        _sut = new PostgresPipelineRunHistoryService(
            new TestDbContextFactory(_dbOptions),
            _mockLogger.Object);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
            Directory.Delete(_tempBase, recursive: true);
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── CleanupExpiredWorkspaces ──────────────────────────────────────────

    [Fact]
    public void CleanupExpiredWorkspaces_NegativeRetentionDays_DoesNothing()
    {
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempBase,
            FailedWorkspaceRetentionDays = -1
        };

        // No exception, no DB query side-effects — assert the config was not modified
        _sut.CleanupExpiredWorkspaces(config);

        config.FailedWorkspaceRetentionDays.Should().Be(-1, "negative retention must not be mutated");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_NullConfig_ThrowsArgumentNullException()
    {
        var act = () => _sut.CleanupExpiredWorkspaces(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void CleanupExpiredWorkspaces_ExpiredFailedRun_DeletesWorkspace()
    {
        // Arrange: insert a Failed run that completed 8 days ago (beyond 7-day retention)
        var runId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddDays(-8);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#1",
                IssueTitle = "Test",
                FinalStep = PipelineStep.Failed,
                StartedAt = completedAt.AddHours(-1),
                CompletedAt = completedAt,
                RunType = PipelineRunType.Implementation
            });
            db.SaveChanges();
        }

        // Create the corresponding workspace directory
        var workspaceDir = Path.Combine(_tempBase, runId.ToString());
        Directory.CreateDirectory(workspaceDir);
        File.WriteAllText(Path.Combine(workspaceDir, "artifact.bin"), "data");

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempBase,
            FailedWorkspaceRetentionDays = 7
        };

        _sut.CleanupExpiredWorkspaces(config);

        Directory.Exists(workspaceDir).Should().BeFalse(
            "expired failed run's workspace must be deleted by cleanup");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_CompletedRun_IsRetained()
    {
        // Arrange: Completed runs must NOT be cleaned up regardless of age
        var runId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddDays(-30);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#2",
                IssueTitle = "Successful run",
                FinalStep = PipelineStep.Completed,
                StartedAt = completedAt.AddHours(-1),
                CompletedAt = completedAt,
                RunType = PipelineRunType.Implementation
            });
            db.SaveChanges();
        }

        var workspaceDir = Path.Combine(_tempBase, runId.ToString());
        Directory.CreateDirectory(workspaceDir);

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempBase,
            FailedWorkspaceRetentionDays = 7
        };

        _sut.CleanupExpiredWorkspaces(config);

        // Query excludes FinalStep == Completed, so workspace must survive
        Directory.Exists(workspaceDir).Should().BeTrue(
            "completed run workspaces must not be deleted by failed-workspace cleanup");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_ActiveRunId_IsExcluded()
    {
        // Arrange: expired failed run, but it's currently active — must not be deleted
        var runId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddDays(-10);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#3",
                IssueTitle = "Active run",
                FinalStep = PipelineStep.Failed,
                StartedAt = completedAt.AddHours(-1),
                CompletedAt = completedAt,
                RunType = PipelineRunType.Implementation
            });
            db.SaveChanges();
        }

        var workspaceDir = Path.Combine(_tempBase, runId.ToString());
        Directory.CreateDirectory(workspaceDir);

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempBase,
            FailedWorkspaceRetentionDays = 7
        };

        // Pass the run's ID as active — it must be excluded from the query
        _sut.CleanupExpiredWorkspaces(config, activeRunId: runId.ToString());

        Directory.Exists(workspaceDir).Should().BeTrue(
            "the active run's workspace must be excluded from cleanup");
    }

    [Fact]
    public void CleanupExpiredWorkspaces_RecentFailedRun_IsRetained()
    {
        // Run failed only 2 days ago, within the 7-day retention window
        var runId = Guid.NewGuid();
        var completedAt = DateTimeOffset.UtcNow.AddDays(-2);

        using (var db = new TestPipelineDbContext(_dbOptions))
        {
            db.PipelineRuns.Add(new PipelineRunEntity
            {
                RunId = runId,
                IssueIdentifier = "owner/repo#4",
                IssueTitle = "Recent failure",
                FinalStep = PipelineStep.Failed,
                StartedAt = completedAt.AddHours(-1),
                CompletedAt = completedAt,
                RunType = PipelineRunType.Implementation
            });
            db.SaveChanges();
        }

        var workspaceDir = Path.Combine(_tempBase, runId.ToString());
        Directory.CreateDirectory(workspaceDir);

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = _tempBase,
            FailedWorkspaceRetentionDays = 7
        };

        _sut.CleanupExpiredWorkspaces(config);

        Directory.Exists(workspaceDir).Should().BeTrue(
            "workspace within retention window must not be deleted");
    }

    // ── Test infrastructure ───────────────────────────────────────────────

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var rowVersion = entityType.FindProperty("RowVersion");
                if (rowVersion != null)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var indexesToRemove = entityType.GetIndexes()
                    .Where(i => i.GetFilter() != null)
                    .ToList();
                foreach (var index in indexesToRemove)
                    entityType.RemoveIndex(index);
            }
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
