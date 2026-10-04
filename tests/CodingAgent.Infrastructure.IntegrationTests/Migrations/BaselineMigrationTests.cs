using AwesomeAssertions;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Migrations;

/// <summary>
/// The original migrations were squashed into one baseline that reuses the ID of the newest original
/// migration. A database that applied every original migration must see nothing pending, and a database
/// that stopped earlier must fail at startup with a clear message instead of re-running the baseline.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BaselineMigrationTests : IAsyncLifetime
{
    private const string BaselineId = "20260930213632_DropConsolidationRuns";

    // Null until InitializeAsync runs, so Docker-less environments can skip these tests.
    private PostgreSqlContainer? _container;
    private ServiceProvider? _provider;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("baseline_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();
            await _container.StartAsync();
        }
        catch (Exception)
        {
            // Docker is unavailable (e.g. the local agent quality-gate runner); the tests return early.
            _container = null;
        }
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null)
            await _provider.DisposeAsync();
        if (_container is not null)
            await _container.DisposeAsync();
    }

    [Fact]
    public void Assembly_StartsWithTheBaseline()
    {
        using var db = new PipelineDbContext(
            new DbContextOptionsBuilder<PipelineDbContext>().UseNpgsql("Host=unused").Options);

        db.Database.GetMigrations().First().Should().Be(BaselineId);
    }

    [Fact]
    public async Task HandleMigrations_DatabaseWithEveryOriginalMigration_DoesNotReapplyTheBaseline()
    {
        if (_container is null) return;
        // A production database: the schema of every original migration (identical to the baseline's) and
        // their IDs in the history, the newest of which is the baseline's ID.
        await using (var db = CreateDbContext())
            await db.GetService<IMigrator>().MigrateAsync(BaselineId);
        await AddToMigrationHistoryAsync("20260626190950_InitialCreate", "20260928175544_RemoveProjectTemplateIds");

        // Re-running the baseline would fail on the existing tables; migrations added after it apply normally.
        await CreateStartupService().HandleMigrationsAsync(CancellationToken.None);

        await using var check = CreateDbContext();
        (await check.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task HandleMigrations_DatabaseStoppedBeforeBaseline_FailsWithUpgradeHint()
    {
        if (_container is null) return;
        await CreateMigrationHistoryAsync("20260928175544_RemoveProjectTemplateIds");

        var act = () => CreateStartupService().HandleMigrationsAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage($"*20260928175544_RemoveProjectTemplateIds, before {BaselineId}*build from before the squash*");
    }

    /// <summary>Creates the EF history table holding only the given migration IDs.</summary>
    private async Task CreateMigrationHistoryAsync(params string[] migrationIds)
    {
        await using (var db = CreateDbContext())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TABLE "__EFMigrationsHistory" (
                    "MigrationId" character varying(150) NOT NULL PRIMARY KEY,
                    "ProductVersion" character varying(32) NOT NULL)
                """);
        }
        await AddToMigrationHistoryAsync(migrationIds);
    }

    private async Task AddToMigrationHistoryAsync(params string[] migrationIds)
    {
        await using var db = CreateDbContext();
        foreach (var id in migrationIds)
            await db.Database.ExecuteSqlAsync($"""INSERT INTO "__EFMigrationsHistory" VALUES ({id}, '10.0.12')""");
    }

    private DatabaseStartupService CreateStartupService()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PipelineDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddDistributedLockProvider(ConnectionString);
        _provider = services.BuildServiceProvider();

        return new DatabaseStartupService(
            _provider.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            _provider.GetRequiredService<IDistributedLockProvider>(),
            new ConfigurationBuilder().Build(),
            Serilog.Core.Logger.None);
    }

    private PipelineDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<PipelineDbContext>().UseNpgsql(ConnectionString).Options);

    private string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
}
