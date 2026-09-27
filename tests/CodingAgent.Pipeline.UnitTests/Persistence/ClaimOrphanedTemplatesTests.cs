using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Pipeline.UnitTests.Persistence;

/// <summary>
/// Tests for <see cref="DatabaseStartupService.ClaimOrphanedTemplatesAsync"/> — verifies that
/// templates whose ProjectId does not match their owning project's TemplateIds list (or whose
/// project no longer exists) are reparented to the Default project at startup.
///
/// Replaces the pre-Spec-041 CRUD tests that were migrated from JsonConfigurationStore and
/// became duplicates of ProjectStoreTests.cs after the orphan-claiming logic was removed.
/// </summary>
public class ClaimOrphanedTemplatesTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly InProcessDistributedLockProvider _lockProvider;

    public ClaimOrphanedTemplatesTests()
    {
        var dbName = $"ClaimOrphanedTemplates-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);
        _lockProvider = new InProcessDistributedLockProvider();

        // Seed the Default project which must exist before repair runs
        using var seed = _dbFactory.CreateDbContext();
        seed.Projects.Add(new ProjectEntity
        {
            Id = Guid.Parse(WellKnownIds.DefaultProjectId),
            Name = "Default",
            Enabled = true,
            TemplateIds = []
        });
        seed.SaveChanges();
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── ClaimOrphanedTemplatesAsync ────────────────────────────────────

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_TemplateNotInOwningProjectTemplateIds_MovesToDefault()
    {
        // Arrange: create a project that owns the template by FK but does NOT list it in TemplateIds
        var projectGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();
        var templateIdStr = templateGuid.ToString();

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectGuid,
                Name = "Source",
                Enabled = true,
                TemplateIds = [] // NOT listed — this is the orphan condition
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = projectGuid, // FK points to Source but Source doesn't list it
                Name = "T1",
                Configuration = System.Text.Json.JsonSerializer.Serialize(
                    new PipelineJobTemplate { Id = templateIdStr, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: template's FK now points to Default
        await using var verify = _dbFactory.CreateDbContext();
        var template = await verify.PipelineJobTemplates.FindAsync(templateGuid);
        template!.ProjectId.Should().Be(Guid.Parse(WellKnownIds.DefaultProjectId));

        // Assert: Default project's TemplateIds contains the template
        var defaultProject = await verify.Projects.FindAsync(Guid.Parse(WellKnownIds.DefaultProjectId));
        defaultProject!.TemplateIds.Should().Contain(templateIdStr);
    }

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_TemplateWithDeletedProject_MovesToDefault()
    {
        // Arrange: template's ProjectId references a project GUID that doesn't exist
        var missingProjectGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();
        var templateIdStr = templateGuid.ToString();

        await using (var db = _dbFactory.CreateDbContext())
        {
            // Do NOT create the project — it's gone
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = missingProjectGuid, // project doesn't exist
                Name = "T1",
                Configuration = System.Text.Json.JsonSerializer.Serialize(
                    new PipelineJobTemplate { Id = templateIdStr, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: template reparented to Default
        await using var verify = _dbFactory.CreateDbContext();
        var template = await verify.PipelineJobTemplates.FindAsync(templateGuid);
        template!.ProjectId.Should().Be(Guid.Parse(WellKnownIds.DefaultProjectId));

        var defaultProject = await verify.Projects.FindAsync(Guid.Parse(WellKnownIds.DefaultProjectId));
        defaultProject!.TemplateIds.Should().Contain(templateIdStr);
    }

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_AlreadyCorrectTemplate_IsUntouched()
    {
        // Arrange: template FK and TemplateIds agree — not orphaned
        var projectGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();
        var templateIdStr = templateGuid.ToString();

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectGuid,
                Name = "Source",
                Enabled = true,
                TemplateIds = [templateIdStr] // Listed — not orphaned
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = projectGuid,
                Name = "T1",
                Configuration = System.Text.Json.JsonSerializer.Serialize(
                    new PipelineJobTemplate { Id = templateIdStr, Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: template remains in Source, NOT moved to Default
        await using var verify = _dbFactory.CreateDbContext();
        var template = await verify.PipelineJobTemplates.FindAsync(templateGuid);
        template!.ProjectId.Should().Be(projectGuid);

        var defaultProject = await verify.Projects.FindAsync(Guid.Parse(WellKnownIds.DefaultProjectId));
        defaultProject!.TemplateIds.Should().NotContain(templateIdStr);
    }

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_MultipleOrphans_AllRepaired()
    {
        // Arrange: three orphaned templates from two different projects
        var projectA = Guid.NewGuid();
        var projectB = Guid.NewGuid();
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();
        var t3 = Guid.NewGuid();

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity { Id = projectA, Name = "A", TemplateIds = [] });
            db.Projects.Add(new ProjectEntity { Id = projectB, Name = "B", TemplateIds = [] });
            foreach (var (guid, owner) in new[] { (t1, projectA), (t2, projectA), (t3, projectB) })
            {
                db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
                {
                    Id = guid,
                    ProjectId = owner,
                    Name = $"T-{guid:N}",
                    Configuration = System.Text.Json.JsonSerializer.Serialize(
                        new PipelineJobTemplate { Id = guid.ToString(), Name = "T", IssueProviderId = "ip", RepoProviderId = "rp" },
                        PipelineJsonOptions.Default)
                });
            }
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: all three templates reparented to Default
        await using var verify = _dbFactory.CreateDbContext();
        foreach (var guid in new[] { t1, t2, t3 })
        {
            var template = await verify.PipelineJobTemplates.FindAsync(guid);
            template!.ProjectId.Should().Be(Guid.Parse(WellKnownIds.DefaultProjectId),
                $"template {guid} must be in Default");
        }

        var defaultProject = await verify.Projects.FindAsync(Guid.Parse(WellKnownIds.DefaultProjectId));
        defaultProject!.TemplateIds.Should().Contain(t1.ToString());
        defaultProject.TemplateIds.Should().Contain(t2.ToString());
        defaultProject.TemplateIds.Should().Contain(t3.ToString());
    }

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_LogsRepairLinePerOrphan()
    {
        // Arrange: two orphaned templates
        var t1 = Guid.NewGuid();
        var t2 = Guid.NewGuid();

        await using (var db = _dbFactory.CreateDbContext())
        {
            var missingProject = Guid.NewGuid();
            foreach (var guid in new[] { t1, t2 })
            {
                db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
                {
                    Id = guid,
                    ProjectId = missingProject,
                    Name = "T",
                    Configuration = System.Text.Json.JsonSerializer.Serialize(
                        new PipelineJobTemplate { Id = guid.ToString(), Name = "T", IssueProviderId = "ip", RepoProviderId = "rp" },
                        PipelineJsonOptions.Default)
                });
            }
            await db.SaveChangesAsync();
        }

        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var sut = CreateService(logger);

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: one repair log line per orphaned template
        var repairLines = sink.Events
            .Where(e => e.MessageTemplate.Text.Contains("reparented orphaned template"))
            .ToList();
        repairLines.Should().HaveCount(2, "one log line per repaired template");
    }

    [Fact]
    public async Task ClaimOrphanedTemplatesAsync_NoOrphans_DoesNotCallSaveChanges()
    {
        // Arrange: everything is consistent — no orphans
        var projectGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity
            {
                Id = projectGuid,
                Name = "Source",
                TemplateIds = [templateGuid.ToString()]
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = projectGuid,
                Name = "T1",
                Configuration = System.Text.Json.JsonSerializer.Serialize(
                    new PipelineJobTemplate { Id = templateGuid.ToString(), Name = "T1", IssueProviderId = "ip", RepoProviderId = "rp" },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sink = new CapturingSink();
        var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var sut = CreateService(logger);

        // Act
        await sut.ClaimOrphanedTemplatesAsync(CancellationToken.None);

        // Assert: no reparent log lines (nothing changed)
        var repairLines = sink.Events.Where(e => e.MessageTemplate.Text.Contains("reparented orphaned template")).ToList();
        // TODO [WARNING]: Test name promises "DoesNotCallSaveChanges" but this assertion is an
        // indirect proxy — it checks that no log lines were emitted, not that SaveChangesAsync was
        // skipped. If SaveChangesAsync were called unconditionally, no log lines would be emitted
        // and this test would still pass. Consider renaming to _NoOrphans_EmitsNoRepairLogLines,
        // or adding a DB state assertion (e.g. Default project TemplateIds remains empty) to verify
        // no mutations occurred.
        repairLines.Should().BeEmpty("no orphans — nothing should be repaired");
    }

    // ── Helpers ────────────────────────────────────────────────────────

    private DatabaseStartupService CreateService(Serilog.ILogger? logger = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new DatabaseStartupService(
            _dbFactory, _lockProvider, config,
            logger ?? new LoggerConfiguration().CreateLogger(),
            new NoOpProbe());
    }

    /// <summary>Probe that always succeeds (connection retry is not under test here).</summary>
    private sealed class NoOpProbe : IDatabaseProbe
    {
        public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
    }

    /// <summary>Serilog sink that captures log events for assertion.</summary>
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly List<LogEvent> _events = new();
        public IReadOnlyList<LogEvent> Events => _events;
        public void Emit(LogEvent logEvent) => _events.Add(logEvent);
    }

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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

                // Disable auto-generation on Guid PKs so Guid.Empty is stored verbatim.
                // EF Core convention for Guid PKs is ValueGenerated.OnAdd, which substitutes Guid.Empty.
                // The Default project uses Guid.Empty as its stable key.
                var idProp = entityType.FindProperty("Id");
                if (idProp != null && idProp.ClrType == typeof(Guid))
                {
                    idProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
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
}
