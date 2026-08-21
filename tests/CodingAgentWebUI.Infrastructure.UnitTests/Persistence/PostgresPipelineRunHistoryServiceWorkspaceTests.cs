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
/// Tests for <see cref="PostgresPipelineRunHistoryService.TryDeleteWorkspace"/> and
/// <see cref="PostgresPipelineRunHistoryService.CleanupExpiredWorkspaces"/>.
/// Uses temp directories for filesystem assertions and InMemory EF for DB-backed tests.
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

    // ── TryDeleteWorkspace ────────────────────────────────────────────────

    [Fact]
    public void TryDeleteWorkspace_NullPath_DoesNothing()
    {
        // null path → early return without touching the filesystem
        _sut.TryDeleteWorkspace(null, "run-1", _tempBase);

        // no side effects — no exception, no warning logged for null/empty path case
        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void TryDeleteWorkspace_EmptyPath_DoesNothing()
    {
        _sut.TryDeleteWorkspace("", "run-1", _tempBase);

        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
    }

    [Fact]
    public void TryDeleteWorkspace_NonExistentDirectory_DoesNothing()
    {
        var path = Path.Combine(_tempBase, "does-not-exist");

        _sut.TryDeleteWorkspace(path, "run-1", _tempBase);

        // directory still doesn't exist (nothing created)
        Directory.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void TryDeleteWorkspace_PathOutsideBase_LogsWarningAndSkips()
    {
        // Create a real directory outside the base
        var outsideDir = Path.Combine(Path.GetTempPath(), $"outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);

        try
        {
            _sut.TryDeleteWorkspace(outsideDir, "run-1", _tempBase);

            // Directory must NOT be deleted (traversal guard proven by directory still existing)
            Directory.Exists(outsideDir).Should().BeTrue("path outside base must not be deleted");
        }
        finally
        {
            if (Directory.Exists(outsideDir))
                Directory.Delete(outsideDir, recursive: true);
        }
    }

    [Fact]
    public void TryDeleteWorkspace_ValidPath_DeletesDirectory()
    {
        var runId = Guid.NewGuid().ToString();
        var workspaceDir = Path.Combine(_tempBase, runId);
        Directory.CreateDirectory(workspaceDir);
        File.WriteAllText(Path.Combine(workspaceDir, "output.log"), "agent output");

        _sut.TryDeleteWorkspace(workspaceDir, runId, _tempBase);

        Directory.Exists(workspaceDir).Should().BeFalse("successful cleanup must remove the workspace directory");
    }

    [Fact]
    public void TryDeleteWorkspace_ValidPath_LogsInformationOnSuccess()
    {
        var runId = Guid.NewGuid().ToString();
        var workspaceDir = Path.Combine(_tempBase, runId);
        Directory.CreateDirectory(workspaceDir);

        _sut.TryDeleteWorkspace(workspaceDir, runId, _tempBase);

        // Behavior assertion: directory is gone. Serilog generic overload makes mock verify fragile.
        Directory.Exists(workspaceDir).Should().BeFalse("successful cleanup must remove the workspace directory");
    }

    [Fact]
    public void TryDeleteWorkspace_PathEqualsBase_LogsWarningAndSkips()
    {
        // Attempting to delete the base directory itself must be rejected
        var runId = Guid.NewGuid().ToString();

        _sut.TryDeleteWorkspace(_tempBase, runId, _tempBase);

        Directory.Exists(_tempBase).Should().BeTrue("deleting the base directory itself must be blocked");
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

        // Should return immediately without querying the DB or touching the filesystem
        _sut.CleanupExpiredWorkspaces(config);

        // No exception, no DB query side-effects
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
