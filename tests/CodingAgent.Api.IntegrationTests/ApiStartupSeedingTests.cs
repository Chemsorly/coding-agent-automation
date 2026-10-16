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
/// on real Postgres. <c>StartupSeedingConcurrencyTests</c> in <c>CodingAgent.Infrastructure.IntegrationTests</c>
/// proves that with two replicas and real advisory locks.
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
        // NOTE: InMemory EF disables RowVersion concurrency tokens (see class-level XML doc), and both
        // tasks share one DatabaseStartupService instance, so this test only proves the two calls
        // serialise via the in-process lock provider without deadlock and stay idempotent. The
        // multi-replica guarantee is covered by StartupSeedingConcurrencyTests in
        // CodingAgent.Infrastructure.IntegrationTests (two service instances, real advisory locks).
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

    // ── AC3: MoveRepositorySettingsToTemplatesAsync — repository settings migrated to templates ────

    /// <summary>
    /// When a repository provider carries any of the five repository-settings fields and at least
    /// one enabled and one disabled template reference it, the fields are copied onto both templates
    /// (respecting null-means-unset) and cleared from the provider. Covers the primary migration path.
    /// </summary>
    [Fact]
    public async Task MoveRepositorySettings_CopiesFieldsFromProviderOntoItsTemplates()
    {
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var providerGuid = Guid.NewGuid();
        var enabledTemplateGuid = Guid.NewGuid();
        var disabledTemplateGuid = Guid.NewGuid();
        const string providerId = "rp-migrate-1";

        var provider = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Migrate Me",
            Settings = new Dictionary<string, string> { ["owner"] = "org" },
            RequiredLabels = ["kiro", "dotnet"],
            Secrets = new Dictionary<string, string> { ["NPM_TOKEN"] = "s3cr3t" },
            SetupSteps = [new SetupStep { Name = "restore", Command = "dotnet restore" }],
            SteeringContent = "## Architecture\nFoo",
            BlacklistedPaths = [".kiro"],
        };
        var enabledTemplate = new PipelineJobTemplate
        {
            Id = enabledTemplateGuid.ToString(),
            Name = "Enabled",
            IssueProviderId = "ip-1",
            RepoProviderId = providerId,
            Enabled = true,
        };
        var disabledTemplate = new PipelineJobTemplate
        {
            Id = disabledTemplateGuid.ToString(),
            Name = "Disabled",
            IssueProviderId = "ip-1",
            RepoProviderId = providerId,
            Enabled = false,
        };

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity { Id = defaultGuid, Name = "Default", Enabled = true });
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerGuid,
                Kind = ProviderKind.Repository,
                DisplayName = "Migrate Me",
                ProviderType = "GitHub",
                Configuration = System.Text.Json.JsonSerializer.Serialize(provider, PipelineJsonOptions.Default)
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = enabledTemplateGuid,
                ProjectId = defaultGuid,
                Name = "Enabled",
                Configuration = System.Text.Json.JsonSerializer.Serialize(enabledTemplate, PipelineJsonOptions.Default)
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = disabledTemplateGuid,
                ProjectId = defaultGuid,
                Name = "Disabled",
                Configuration = System.Text.Json.JsonSerializer.Serialize(disabledTemplate, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        await CreateService().RunStartupSeedingAsync(CancellationToken.None);

        await using var check = _dbFactory.CreateDbContext();

        // Provider's five fields must be cleared
        var updatedProvider = await check.ProviderConfigs.FindAsync(providerGuid);
        var providerConfig = System.Text.Json.JsonSerializer.Deserialize<ProviderConfig>(
            updatedProvider!.Configuration!, PipelineJsonOptions.Lenient);
        providerConfig!.RequiredLabels.Should().BeNullOrEmpty("five fields must be cleared from the provider");
        providerConfig.Secrets.Should().BeNullOrEmpty();
        providerConfig.SetupSteps.Should().BeNullOrEmpty();
        providerConfig.SteeringContent.Should().BeNullOrWhiteSpace();
        providerConfig.BlacklistedPaths.Should().BeNullOrEmpty();

        // Both templates (enabled and disabled) must now carry the fields
        foreach (var templateGuid in new[] { enabledTemplateGuid, disabledTemplateGuid })
        {
            var entity = await check.PipelineJobTemplates.FindAsync(templateGuid);
            var tmpl = System.Text.Json.JsonSerializer.Deserialize<PipelineJobTemplate>(
                entity!.Configuration!, PipelineJsonOptions.Lenient);
            tmpl!.RequiredLabels.Should().BeEquivalentTo(["kiro", "dotnet"],
                $"template {templateGuid} must receive RequiredLabels from the provider");
            tmpl.Secrets.Should().ContainKey("NPM_TOKEN").WhoseValue.Should().Be("s3cr3t");
            tmpl.SetupSteps.Should().HaveCount(1);
            tmpl.SteeringContent.Should().Be("## Architecture\nFoo");
            tmpl.BlacklistedPaths.Should().BeEquivalentTo([".kiro"]);
        }
    }

    /// <summary>
    /// When a template already has a non-empty value for a field, the migration must NOT
    /// overwrite it — even if the provider also has a value for that field.
    /// </summary>
    [Fact]
    public async Task MoveRepositorySettings_DoesNotOverwriteExistingTemplateValue()
    {
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var providerGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();
        const string providerId = "rp-migrate-2";

        var provider = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "No Overwrite",
            Settings = new Dictionary<string, string>(),
            RequiredLabels = ["from-provider"],
            SteeringContent = "provider-steering",
        };
        var template = new PipelineJobTemplate
        {
            Id = templateGuid.ToString(),
            Name = "AlreadyHasLabels",
            IssueProviderId = "ip-1",
            RepoProviderId = providerId,
            Enabled = true,
            RequiredLabels = ["existing-label"],  // already set — must not be overwritten
            SteeringContent = null,               // not set — must be copied from provider
        };

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity { Id = defaultGuid, Name = "Default", Enabled = true });
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerGuid,
                Kind = ProviderKind.Repository,
                DisplayName = "No Overwrite",
                ProviderType = "GitHub",
                Configuration = System.Text.Json.JsonSerializer.Serialize(provider, PipelineJsonOptions.Default)
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = defaultGuid,
                Name = "AlreadyHasLabels",
                Configuration = System.Text.Json.JsonSerializer.Serialize(template, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        await CreateService().RunStartupSeedingAsync(CancellationToken.None);

        await using var check = _dbFactory.CreateDbContext();
        var entity = await check.PipelineJobTemplates.FindAsync(templateGuid);
        var tmpl = System.Text.Json.JsonSerializer.Deserialize<PipelineJobTemplate>(
            entity!.Configuration!, PipelineJsonOptions.Lenient);

        tmpl!.RequiredLabels.Should().BeEquivalentTo(["existing-label"],
            "existing non-empty template value must not be overwritten by provider value");
        tmpl.SteeringContent.Should().Be("provider-steering",
            "null template field must be filled from provider");
    }

    /// <summary>
    /// A repository provider whose five fields are set but which no template references
    /// must keep its values and generate a warning log — the migration must not silently discard them.
    /// </summary>
    [Fact]
    public async Task MoveRepositorySettings_KeepsValuesOnUnreferencedProvider()
    {
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var providerGuid = Guid.NewGuid();
        const string providerId = "rp-migrate-3";

        var provider = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Unreferenced",
            Settings = new Dictionary<string, string>(),
            RequiredLabels = ["kiro"],
            SteeringContent = "keep me",
        };

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity { Id = defaultGuid, Name = "Default", Enabled = true });
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerGuid,
                Kind = ProviderKind.Repository,
                DisplayName = "Unreferenced",
                ProviderType = "GitHub",
                Configuration = System.Text.Json.JsonSerializer.Serialize(provider, PipelineJsonOptions.Default)
            });
            // No templates reference this provider
            await db.SaveChangesAsync();
        }

        // Must not throw
        await CreateService().RunStartupSeedingAsync(CancellationToken.None);

        await using var check = _dbFactory.CreateDbContext();
        var updatedProvider = await check.ProviderConfigs.FindAsync(providerGuid);
        var providerConfig = System.Text.Json.JsonSerializer.Deserialize<ProviderConfig>(
            updatedProvider!.Configuration!, PipelineJsonOptions.Lenient);

        providerConfig!.RequiredLabels.Should().BeEquivalentTo(["kiro"],
            "provider settings must be preserved when no template references it");
        providerConfig.SteeringContent.Should().Be("keep me",
            "provider settings must be preserved when no template references it");
    }

    /// <summary>
    /// A second call to <see cref="DatabaseStartupService.RunStartupSeedingAsync"/> after the
    /// migration has already run must change nothing — the migration is idempotent.
    /// </summary>
    [Fact]
    public async Task MoveRepositorySettings_IsIdempotent_SecondRunChangesNothing()
    {
        var defaultGuid = Guid.Parse(WellKnownIds.DefaultProjectId);
        var providerGuid = Guid.NewGuid();
        var templateGuid = Guid.NewGuid();
        const string providerId = "rp-migrate-4";

        var provider = new ProviderConfig
        {
            Id = providerId,
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Idempotent",
            Settings = new Dictionary<string, string>(),
            RequiredLabels = ["kiro"],
        };
        var template = new PipelineJobTemplate
        {
            Id = templateGuid.ToString(),
            Name = "Idempotent",
            IssueProviderId = "ip-1",
            RepoProviderId = providerId,
            Enabled = true,
        };

        await using (var db = _dbFactory.CreateDbContext())
        {
            db.Projects.Add(new ProjectEntity { Id = defaultGuid, Name = "Default", Enabled = true });
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerGuid,
                Kind = ProviderKind.Repository,
                DisplayName = "Idempotent",
                ProviderType = "GitHub",
                Configuration = System.Text.Json.JsonSerializer.Serialize(provider, PipelineJsonOptions.Default)
            });
            db.PipelineJobTemplates.Add(new PipelineJobTemplateEntity
            {
                Id = templateGuid,
                ProjectId = defaultGuid,
                Name = "Idempotent",
                Configuration = System.Text.Json.JsonSerializer.Serialize(template, PipelineJsonOptions.Default)
            });
            await db.SaveChangesAsync();
        }

        var sut = CreateService();

        // First run: migrates
        await sut.RunStartupSeedingAsync(CancellationToken.None);

        // Capture state after first run
        string providerConfigAfterFirst;
        string templateConfigAfterFirst;
        await using (var check = _dbFactory.CreateDbContext())
        {
            providerConfigAfterFirst = (await check.ProviderConfigs.FindAsync(providerGuid))!.Configuration!;
            templateConfigAfterFirst = (await check.PipelineJobTemplates.FindAsync(templateGuid))!.Configuration!;
        }

        // Second run: must not change anything
        await sut.RunStartupSeedingAsync(CancellationToken.None);

        await using var checkAgain = _dbFactory.CreateDbContext();
        var providerConfigAfterSecond = (await checkAgain.ProviderConfigs.FindAsync(providerGuid))!.Configuration!;
        var templateConfigAfterSecond = (await checkAgain.PipelineJobTemplates.FindAsync(templateGuid))!.Configuration!;

        providerConfigAfterSecond.Should().Be(providerConfigAfterFirst,
            "second run must not change the provider configuration (idempotent)");
        templateConfigAfterSecond.Should().Be(templateConfigAfterFirst,
            "second run must not change the template configuration (idempotent)");
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
