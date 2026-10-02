using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="PostgresPipelineRunHistoryService.TryDeleteWorkspace"/>.
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
    // Guard logic (symlink check, path-containment, recursive delete) is tested in
    // WorkspaceDeletionGuardTests. The tests below verify that the Postgres service
    // correctly delegates to the guard without throwing.

    [Fact]
    public void TryDeleteWorkspace_NullPath_DoesNothing()
    {
        // null path → early return without touching the filesystem
        _sut.TryDeleteWorkspace(null, "run-1", _tempBase);

        // TODO: This Warning verify is fragile: WorkspaceDeletionGuard uses Serilog ILogger
        // structured overloads that may not match the Moq mock's generic parameter capture,
        // causing Times.Never to silently pass even if a warning is actually emitted. This
        // is the same fragility noted for the deleted TryDeleteWorkspace_ValidPath_LogsInformationOnSuccess
        // test. Consider removing the verify or replacing with a behavior-only assertion.
        // no side effects — no exception, no warning logged for null/empty path case
        _mockLogger.Verify(l => l.Warning(It.IsAny<string>(), It.IsAny<object[]>()), Times.Never);
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
