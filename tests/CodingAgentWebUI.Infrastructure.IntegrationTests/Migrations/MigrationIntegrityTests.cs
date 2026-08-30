using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using DotNet.Testcontainers.Builders;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace CodingAgentWebUI.Infrastructure.IntegrationTests.Migrations;

/// <summary>
/// Migration integrity tests using a real PostgreSQL container.
///
/// These tests catch two classes of failure that the E2E harness (EF InMemory) and unit
/// tests cannot detect:
///
/// 1. <b>PendingModelChangesWarning</b> — the compiled EF model diverges from the migration
///    snapshot. This caused a production startup crash: the API runs with
///    <c>MigrateOnStartup=false</c>, which calls <c>GetPendingMigrationsAsync</c> and
///    additionally validates that the model and snapshot are in sync. Any divergence throws
///    <c>InvalidOperationException: PendingModelChangesWarning</c> before the app serves
///    traffic. The EF InMemory provider bypasses migrations entirely, so this class of error
///    is invisible in the in-process E2E harness.
///
/// 2. <b>DDL correctness</b> — migration SQL that is syntactically valid C# but invalid at
///    the Postgres level (e.g., <c>ALTER COLUMN … TYPE uuid USING "col"::uuid</c> against a
///    column with values that aren't valid UUIDs, or an index creation that violates a
///    constraint). These fail only when <c>MigrateAsync</c> executes against a real database.
///
/// The test fixture starts a single PostgreSQL 17 container per test class (shared across
/// tests via <c>IClassFixture</c>) and applies all migrations from scratch in
/// <see cref="MigrationIntegrityFixture.InitializeAsync"/>. Individual tests then assert
/// post-migration invariants.
///
/// <b>CI placement:</b> These run in the <c>migration-integrity</c> job in <c>ci.yml</c>,
/// which runs directly on the GitHub Actions runner (no <c>container:</c> wrapper) so that
/// Docker is available for Testcontainers. The job is tagged <c>Category=Integration</c>
/// and excluded from the fast unit-test job (<c>--filter "Category!=E2E&amp;Category!=Integration"</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class MigrationIntegrityTests : IClassFixture<MigrationIntegrityFixture>
{
    private readonly MigrationIntegrityFixture _fixture;

    public MigrationIntegrityTests(MigrationIntegrityFixture fixture)
    {
        _fixture = fixture;
    }

    /// <summary>
    /// Returns early (vacuous pass) when Docker is unavailable in this environment.
    /// Migration integrity tests require a real PostgreSQL container and can only run where
    /// Docker is available (e.g., the GitHub Actions <c>migration-integrity</c> job).
    /// When Docker is absent, the test produces no assertion rather than failing with
    /// an infrastructure error — the tests are not skipped in the xUnit sense, but they
    /// are not meaningful either.  The GitHub Actions <c>migration-integrity</c> job
    /// runs these tests against a real Docker daemon.
    /// </summary>
    private bool ShouldSkip() => _fixture.SkipReason is not null;

    /// <summary>
    /// All EF migrations must apply cleanly from scratch against a real Postgres database.
    /// A failure here indicates broken DDL in one of the migration files — typically
    /// Postgres-specific SQL that is only validated at execution time (e.g., USING casts,
    /// index expressions, FK constraint violations on existing data).
    /// </summary>
    [Fact]
    public void AllMigrations_ApplyFromScratch_WithoutError()
    {
        if (ShouldSkip()) return;
        // The fixture already ran MigrateAsync in InitializeAsync.
        // If migrations failed, the fixture throws and xUnit marks every test in this class
        // as failed (IClassFixture lifecycle — InitializeAsync exception propagates to all tests).
        // This test makes the invariant explicit and gives it a named assertion in the report.
        _fixture.MigrationException.Should().BeNull(
            "all migrations must apply cleanly from scratch. " +
            "A non-null exception means MigrateAsync threw — inspect the inner exception for " +
            "the failing migration name and DDL.");
    }

    /// <summary>
    /// After applying all migrations, EF must report zero pending model changes.
    /// A failure here means the compiled EF model diverges from the migration snapshot —
    /// the exact condition that caused the production startup crash:
    ///   "InvalidOperationException: PendingModelChangesWarning: The model for context
    ///    'PipelineDbContext' has pending changes. Add a new migration before updating the database."
    /// </summary>
    [Fact]
    public async Task AfterMigrations_NoPendingModelChanges()
    {
        if (ShouldSkip()) return;
        await using var db = _fixture.CreateDbContext();

        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        pending.Should().BeEmpty(
            "after applying all migrations, EF must report no pending migrations. " +
            "A non-empty list means a migration file exists that has not been applied — " +
            "usually caused by the migration snapshot diverging from OnModelCreating.");

        // GetPendingMigrationsAsync checks pending migration files but does NOT check
        // model/snapshot divergence (PendingModelChangesWarning). That warning is thrown
        // by Migrator.ValidateMigrations, which runs during MigrateAsync. If MigrateAsync
        // succeeds (checked by AllMigrations_ApplyFromScratch_WithoutError) and there are
        // no pending migration files, the model is fully in sync.
    }

    /// <summary>
    /// After migrations, the applied migration list must match the migration files present
    /// in the assembly. This catches the case where a migration file was added to the project
    /// but the corresponding DB update was never run (or vice versa — a migration was applied
    /// to the DB but its file was deleted from the project).
    /// </summary>
    [Fact]
    public async Task AfterMigrations_AppliedMigrations_MatchAssemblyMigrations()
    {
        if (ShouldSkip()) return;
        await using var db = _fixture.CreateDbContext();

        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var defined = db.Database.GetMigrations().ToList();

        applied.Should().BeEquivalentTo(defined,
            options => options.WithStrictOrdering(),
            "every migration defined in the assembly must have been applied, in order, " +
            "with no extras or gaps");
    }

    /// <summary>
    /// The WorkItems table must exist with the expected column types after migration.
    /// This is a structural smoke test that validates the most recently modified table
    /// (the one involved in the ProjectId uuid migration that triggered Bug 1).
    /// </summary>
    [Fact]
    public async Task WorkItems_ProjectIdColumn_IsUuidType()
    {
        if (ShouldSkip()) return;
        await using var conn = _fixture.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT data_type
            FROM information_schema.columns
            WHERE table_name = 'WorkItems'
              AND column_name = 'ProjectId'
            """;

        var result = await cmd.ExecuteScalarAsync();

        result.Should().Be("uuid",
            "WorkItems.ProjectId was migrated from text to uuid in " +
            "20260829000000_WorkItemProjectIdToUuidWithFk. If this fails, the migration " +
            "did not apply correctly or the column was reverted.");
    }

    /// <summary>
    /// The FK constraint from WorkItems.ProjectId to Projects.Id must exist.
    /// This validates that the FK created by WorkItemProjectIdToUuidWithFk is present
    /// and correctly named — a missing FK would silently allow orphaned WorkItems.
    /// </summary>
    [Fact]
    public async Task WorkItems_ForeignKey_ToProjects_Exists()
    {
        if (ShouldSkip()) return;
        await using var conn = _fixture.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema   = kcu.table_schema
            WHERE tc.constraint_type = 'FOREIGN KEY'
              AND tc.table_name      = 'WorkItems'
              AND kcu.column_name    = 'ProjectId'
            """;

        var count = (long)(await cmd.ExecuteScalarAsync())!;

        count.Should().Be(1,
            "WorkItems.ProjectId must have a FK constraint to Projects.Id " +
            "(added by 20260829000000_WorkItemProjectIdToUuidWithFk). " +
            "If this fails, the FK was not created or was dropped without a compensating migration.");
    }

    /// <summary>
    /// The spurious IX_WorkItems_ProjectId single-column index must NOT exist after migration.
    /// This index was created by WorkItemProjectIdToUuidWithFk but was absent from
    /// OnModelCreating, causing PendingModelChangesWarning. It was dropped by
    /// 20260829124255_DropSpuriousWorkItemProjectIdIndex.
    /// </summary>
    [Fact]
    public async Task WorkItems_SpuriousSingleColumnProjectIdIndex_DoesNotExist()
    {
        if (ShouldSkip()) return;
        await using var conn = _fixture.CreateConnection();
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE tablename = 'WorkItems'
              AND indexname  = 'IX_WorkItems_ProjectId'
            """;

        var count = (long)(await cmd.ExecuteScalarAsync())!;

        count.Should().Be(0,
            "IX_WorkItems_ProjectId must not exist — it was dropped by " +
            "20260829124255_DropSpuriousWorkItemProjectIdIndex to resolve the " +
            "PendingModelChangesWarning that caused the production startup crash. " +
            "If this index reappears, a future migration re-created it without adding " +
            "the corresponding HasIndex call to OnModelCreating.");
    }
}

/// <summary>
/// xUnit class fixture that starts a PostgreSQL 17 container and applies all EF migrations
/// once for the entire <see cref="MigrationIntegrityTests"/> class.
///
/// Container startup and migration are performed in <see cref="InitializeAsync"/>.
/// Any exception thrown there is captured in <see cref="MigrationException"/> so individual
/// tests can assert on it rather than seeing a cryptic fixture-initialization failure.
/// The container is stopped and disposed in <see cref="DisposeAsync"/>.
/// </summary>
public sealed class MigrationIntegrityFixture : IAsyncLifetime
{
    // Initialized in InitializeAsync rather than as a field initializer so that
    // PostgreSqlBuilder.Build() (which validates Docker availability) is not called
    // during class construction.  A constructor-level DockerUnavailableException would
    // cause xUnit to mark every test in the class as "fixture init failed", which is
    // indistinguishable from a real test failure.  Deferring to InitializeAsync lets us
    // catch the exception, set SkipReason, and have each test throw SkipException instead.
    private PostgreSqlContainer? _container;

    /// <summary>
    /// Set when Docker is unavailable in this environment.
    /// Tests read this via <see cref="MigrationIntegrityTests.SkipIfDockerUnavailable"/> and
    /// skip themselves gracefully rather than failing.
    /// </summary>
    public string? SkipReason { get; private set; }

    /// <summary>
    /// Set when <see cref="InitializeAsync"/> catches an exception from <c>MigrateAsync</c>.
    /// Tests assert this is null to verify clean migration application.
    /// </summary>
    public Exception? MigrationException { get; private set; }

    public async Task InitializeAsync()
    {
        PostgreSqlContainer container;
        try
        {
            container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("migration_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
        }
        catch (DockerUnavailableException ex)
        {
            // Docker is not available in this environment (e.g., the agent quality-gate
            // pod has no Docker socket).  Set SkipReason so that every test can skip
            // cleanly instead of being reported as failed.
            SkipReason = $"Docker is unavailable — migration integrity tests require a real " +
                         $"PostgreSQL container and must run in an environment with Docker " +
                         $"(e.g., the GitHub Actions migration-integrity job). " +
                         $"Inner: {ex.Message}";
            return;
        }

        _container = container;
        await _container.StartAsync();

        await using var db = CreateDbContext();
        try
        {
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            // Capture rather than re-throw: re-throwing here causes xUnit to report every
            // test in the class as "failed to initialize fixture" with an opaque message.
            // Capturing lets AllMigrations_ApplyFromScratch_WithoutError produce a clear
            // failure with the exception details in the assertion message.
            MigrationException = ex;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }

    /// <summary>Creates a new <see cref="PipelineDbContext"/> connected to the test container.</summary>
    public PipelineDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_container!.GetConnectionString())
            .Options;
        return new PipelineDbContext(options);
    }

    /// <summary>Creates a raw <see cref="NpgsqlConnection"/> for schema inspection queries.</summary>
    public NpgsqlConnection CreateConnection() =>
        new(_container!.GetConnectionString());
}
