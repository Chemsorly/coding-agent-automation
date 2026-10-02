using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Serilog;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Integration tests for <see cref="DatabaseStartupService.RunStartupSeedingAsync"/>.
///
/// These tests do NOT use <see cref="ApiWebApplicationFactory"/> or join
/// <see cref="ApiIntegrationTestCollection"/>. Reasons:
/// <list type="bullet">
///   <item><see cref="ApiWebApplicationFactory"/> hardcodes <c>Database__SkipStartupInit=true</c>,
///         which short-circuits the seeding path entirely.</item>
///   <item>Creating a second <c>WebApplicationFactory&lt;Program&gt;</c> in the same test process
///         would trigger the "logger already frozen" Serilog issue documented in
///         <see cref="ApiWebApplicationFactory"/>.</item>
///   <item><see cref="DatabaseStartupService"/> is not registered in DI — it is constructed directly
///         inside <c>RunApiMigrationsAsync</c>. Tests must construct it manually.</item>
/// </list>
///
/// The lock provider is obtained via <see cref="LockingServiceCollectionExtensions.AddDistributedLockProvider"/>
/// (passing null → in-process implementation), because <c>InProcessDistributedLockProvider</c> is internal
/// to <c>CodingAgent.Infrastructure.Persistence</c>.
///
/// InMemory EF limitation: <c>RowVersion</c> concurrency tokens are disabled in
/// <see cref="InMemoryPipelineDbContext"/> (InMemory cannot enforce xmin-style tokens).
/// Therefore the concurrent-startup test proves the two calls serialise without deadlock,
/// but cannot prove the lock eliminates <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>
/// on real Postgres. A Postgres concurrency test belongs in
/// <c>CodingAgent.Infrastructure.IntegrationTests</c>.
/// </summary>
public sealed class ApiStartupSeedingTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly InMemoryDbContextFactory _dbFactory;
    private readonly IDistributedLockProvider _lockProvider;

    public ApiStartupSeedingTests()
    {
        var dbName = $"ApiStartupSeeding-{Guid.NewGuid():N}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        // EnsureCreated sets up the schema so Add/SaveChanges work without migrations.
        using var ctx = new InMemoryPipelineDbContext(_dbOptions);
        ctx.Database.EnsureCreated();

        _dbFactory = new InMemoryDbContextFactory(_dbOptions);

        // Resolve IDistributedLockProvider through DI so we get the in-process implementation
        // without needing access to the internal InProcessDistributedLockProvider class.
        var services = new ServiceCollection();
        services.AddDistributedLockProvider(connectionString: null); // null → InProcess
        _lockProvider = services.BuildServiceProvider().GetRequiredService<IDistributedLockProvider>();
    }

    public void Dispose()
    {
        using var db = new InMemoryPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    // ── AC1: Default project + reviewer configs created on empty database ─────────

    [Fact]
    public async Task RunStartupSeeding_EmptyDatabase_CreatesDefaultProjectAndReviewerConfigs()
    {
        // Arrange: empty database (no Projects, no ReviewerConfigs)
        var sut = CreateService();

        // Act
        await sut.RunStartupSeedingAsync(CancellationToken.None);

        // Assert: Default project row was created
        await using var db = _dbFactory.CreateDbContext();
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var project = await db.Projects.FindAsync(defaultGuid);
        project.Should().NotBeNull("Default project must be created on first startup");
        project!.Name.Should().Be("Default");
        project.Enabled.Should().BeTrue();

        // Assert: reviewer configs were seeded
        // TODO [WARNING]: This only asserts AnyAsync() (table non-empty). It passes even if exactly one row
        // was written when several should have been, or if a future change in SeedDefaultReviewerConfigsIfNeededAsync
        // seeds partial/incorrect configs. Consider asserting the count equals
        // PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count and verifying at least one
        // expected config name to catch partial-seed bugs.
        var hasReviewers = await db.ReviewerConfigs.AnyAsync();
        hasReviewers.Should().BeTrue("default reviewer configs must be seeded when the table is empty");
    }

    [Fact]
    public async Task RunStartupSeeding_EmptyDatabase_IsIdempotent()
    {
        // Running twice must not throw and must not duplicate the Default project row.
        var sut = CreateService();

        await sut.RunStartupSeedingAsync(CancellationToken.None);
        await sut.RunStartupSeedingAsync(CancellationToken.None);

        await using var db = _dbFactory.CreateDbContext();
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var count = await db.Projects.CountAsync(p => p.Id == defaultGuid);
        count.Should().Be(1, "Default project must appear exactly once even after multiple seeding runs");
    }

    // ── AC2: Orphaned template repair ─────────────────────────────────────────────

    [Fact]
    public async Task RunStartupSeeding_OrphanedTemplate_IsReparentedToDefaultProject()
    {
        // Arrange: a template whose project no longer exists
        var missingProjectGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = missingProjectGuid,
                Name = "Orphaned Template",
                Configuration = System.Text.Json.JsonSerializer.Serialize(
                    new PipelineJobTemplate
                    {
                        Id = templateGuid.ToString(),
                        Name = "Orphaned Template",
                        IssueProviderId = "ip",
                        RepoProviderId = "rp"
                    },
                    PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // Act: seeding creates Default project first, then repairs orphaned templates
        await sut.RunStartupSeedingAsync(CancellationToken.None);

        // Assert
        await using var verify = _dbFactory.CreateDbContext();
        var template = await verify.PipelineJobTemplates.FindAsync(templateGuid);
        template.Should().NotBeNull();
        template!.ProjectId.Should().Be(Guid.Parse(WellKnownIds.DefaultProjectId),
            "orphaned template must be reparented to the Default project");
    }

    // ── AC2b: Concurrent startups do not throw ────────────────────────────────────

    [Fact]
    public async Task RunStartupSeeding_TwoConcurrentCalls_DoNotThrow()
    {
        // NOTE: InMemory EF disables RowVersion concurrency tokens (see class-level XML doc),
        // so this test proves the two calls serialise via the in-process lock provider
        // without deadlock — it does NOT prove the lock eliminates DbUpdateConcurrencyException
        // on real Postgres. A Postgres concurrency test belongs in CodingAgent.Infrastructure.IntegrationTests.
        // TODO [WARNING]: Both tasks share a single DatabaseStartupService instance. The two-task race is
        // more cooperative than a true multi-process race: interleaving only happens at async await points,
        // and the async scheduler may run Task1 to completion before Task2 starts, meaning concurrent
        // serialization via the lock is not actually exercised. This test confirms no exception is raised
        // and the result is idempotent, but does not guarantee thread-safety in a stronger sense. A
        // Postgres-level concurrency test with two separate service instances and a real advisory lock
        // is needed to truly verify the DbUpdateConcurrencyException protection.
        var sut = CreateService();

        var t1 = sut.RunStartupSeedingAsync(CancellationToken.None);
        var t2 = sut.RunStartupSeedingAsync(CancellationToken.None);

        // Must not throw
        var act = () => Task.WhenAll(t1, t2);
        await act.Should().NotThrowAsync("two concurrent startup seeding calls must not fail");

        // Idempotency: still exactly one Default project row
        await using var db = _dbFactory.CreateDbContext();
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        (await db.Projects.CountAsync(p => p.Id == defaultGuid)).Should().Be(1);
    }

    // ── AC3: RunApiMigrationsAsync wires RunStartupSeedingAsync correctly ─────────

    [Fact]
    public async Task RunApiMigrationsAsync_WithInMemoryDb_SeedsDefaultProjectAndReviewerConfigs()
    {
        // Arrange: exercise RunApiMigrationsAsync lines 60-62 by passing a pre-built
        // DatabaseStartupService that uses InMemory EF + in-process lock.
        // The 'startupServiceOverride' parameter bypasses the internal 'new DatabaseStartupService()'
        // construction, avoiding HandleMigrationsAsync's relational-provider requirement while still
        // executing all three service calls (WaitForDatabase, HandleMigrations, RunStartupSeeding)
        // through the extension method itself.
        var dbName = $"RunApiMigrations-{Guid.NewGuid():N}";
        var dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using var dbSetup = new InMemoryPipelineDbContext(dbOptions);
        dbSetup.Database.EnsureCreated();

        var factory = new InMemoryDbContextFactory(dbOptions);

        var probeMock = new Mock<IDatabaseProbe>();
        probeMock.Setup(p => p.ProbeAsync(It.IsAny<CancellationToken>()))
                 .Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddDistributedLockProvider(connectionString: null); // in-process
        var sp = services.BuildServiceProvider();
        var lockProvider = sp.GetRequiredService<IDistributedLockProvider>();

        // Config with MigrateOnStartup=false — InMemory EF returns empty pending migrations.
        var svcConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:MigrateOnStartup"] = "false"
            })
            .Build();

        var startupService = new DatabaseStartupService(
            factory, lockProvider, svcConfig, new LoggerConfiguration().CreateLogger(), probeMock.Object);

        // Build a minimal WebApplication — its services are only used for the DI fallback
        // path in RunApiMigrationsAsync (not reached when startupServiceOverride is supplied).
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddDbContextFactory<PipelineDbContext>(o =>
            o.UseInMemoryDatabase(dbName)
             .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning)));
        builder.Services.AddDistributedLockProvider(connectionString: null);
        await using var app = builder.Build();

        var appConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Non-empty host so DatabaseConnectionResolver.Resolve returns non-null.
                ["Database:Host"] = "localhost",
                ["Database:SkipStartupInit"] = "false"
            })
            .Build();

        // Act: passes startupServiceOverride so HandleMigrationsAsync uses InMemory EF
        // (which reports 0 pending migrations with MigrateOnStartup=false via the mock probe).
        await app.RunApiMigrationsAsync(appConfig, startupService);

        // Assert: seeding ran through RunStartupSeedingAsync
        await using var db = factory.CreateDbContext();
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var project = await db.Projects.FindAsync(defaultGuid);
        project.Should().NotBeNull(
            "RunApiMigrationsAsync must seed the Default project via RunStartupSeedingAsync");

        var hasReviewers = await db.ReviewerConfigs.AnyAsync();
        hasReviewers.Should().BeTrue(
            "RunApiMigrationsAsync must seed reviewer configs via RunStartupSeedingAsync");

        using var cleanup = new InMemoryPipelineDbContext(dbOptions);
        cleanup.Database.EnsureDeleted();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────

    private DatabaseStartupService CreateService() =>
        new DatabaseStartupService(
            _dbFactory,
            _lockProvider,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build(),
            new LoggerConfiguration().CreateLogger(),
            probe: null);

    private sealed class InMemoryDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new InMemoryPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }

    /// <summary>
    /// PipelineDbContext subclass for InMemory tests.
    /// Disables RowVersion concurrency tokens and filtered indexes (InMemory provider limitations).
    /// Keys keep the production configuration: the Default project's Guid.Empty ID must be stored as is by
    /// <see cref="PipelineDbContext"/> itself, not by a test override.
    /// </summary>
    private sealed class InMemoryPipelineDbContext : PipelineDbContext
    {
        public InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                // Disable RowVersion — InMemory throws NotSupportedException for concurrency tokens.
                var rowVersionProp = entityType.FindProperty("RowVersion");
                if (rowVersionProp != null)
                {
                    rowVersionProp.IsConcurrencyToken = false;
                    rowVersionProp.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
            }

            // Remove filtered (partial) indexes — not supported by InMemory provider.
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
