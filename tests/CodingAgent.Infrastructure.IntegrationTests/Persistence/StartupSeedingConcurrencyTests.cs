using AwesomeAssertions;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Startup seeding runs on every API replica. These tests start two replicas at once against one real
/// PostgreSQL database: two <see cref="DatabaseStartupService"/> instances, each with its own advisory-lock
/// provider and connections. They check that every seed row is written exactly once and that neither
/// replica fails.
///
/// The seeding tests in <c>CodingAgent.Api.IntegrationTests</c> can't prove this: they share one service
/// instance, their lock is in-process, and EF InMemory has no concurrency tokens.
/// </summary>
[Trait("Category", "Integration")]
public sealed class StartupSeedingConcurrencyTests : IClassFixture<StartupSeedingPostgresFixture>
{
    // Each round empties the seeded tables, so both replicas race on every step again.
    private const int Rounds = 5;

    private readonly StartupSeedingPostgresFixture _fixture;

    public StartupSeedingConcurrencyTests(StartupSeedingPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunStartupSeeding_TwoReplicasAtOnce_SeedsEachRowOnceWithoutError()
    {
        // Skip gracefully when Docker is unavailable; CI runs these in the Docker-enabled integration job.
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");

        var defaultProjectId = Guid.Parse(WellKnownIds.DefaultProjectId);

        for (var round = 1; round <= Rounds; round++)
        {
            var orphanId = await _fixture.ResetToEmptyWithOrphanedTemplateAsync();
            var replicaA = _fixture.CreateReplica();
            var replicaB = _fixture.CreateReplica();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var seedingA = Task.Run(async () =>
            {
                await start.Task;
                await replicaA.RunStartupSeedingAsync(CancellationToken.None);
            });
            var seedingB = Task.Run(async () =>
            {
                await start.Task;
                await replicaB.RunStartupSeedingAsync(CancellationToken.None);
            });
            start.SetResult();

            await FluentActions.Awaiting(() => Task.WhenAll(seedingA, seedingB))
                .Should().NotThrowAsync($"round {round}: concurrent startup seeding must not fail");

            await using var db = _fixture.CreateDbContext();
            (await db.Projects.CountAsync(p => p.Id == defaultProjectId))
                .Should().Be(1, $"round {round}: the Default project is inserted once");
            (await db.ReviewerConfigs.CountAsync())
                .Should().Be(PipelineConfigurationDefaults.DefaultReviewerConfigurations.Count,
                    $"round {round}: the default reviewer configs are seeded once");
            (await db.PipelineJobTemplates.SingleAsync(t => t.Id == orphanId)).ProjectId
                .Should().Be(defaultProjectId, $"round {round}: the orphaned template moves to the Default project");
        }
    }
}

/// <summary>
/// Starts one PostgreSQL container per test class and applies all migrations.
/// </summary>
public sealed class StartupSeedingPostgresFixture : IAsyncLifetime
{
    // Null until InitializeAsync runs: building the container in a field initializer would throw in
    // Docker-less environments before trait filtering can exclude these Integration tests.
    private PostgreSqlContainer? _container;
    private readonly List<ServiceProvider> _providers = [];

    /// <summary>Set when the container fails to start or the migrations fail.</summary>
    public Exception? InitializationException { get; private set; }

    /// <summary>True when the container could not be built, typically because Docker is unavailable.</summary>
    public bool IsDockerUnavailable => _container is null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("seeding_test")
                .WithUsername("test")
                .WithPassword("test")
                .Build();

            await _container.StartAsync();

            await using var db = CreateDbContext();
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            InitializationException = ex;
        }
    }

    public async Task DisposeAsync()
    {
        foreach (var provider in _providers)
            await provider.DisposeAsync();

        if (_container is not null)
            await _container.DisposeAsync();
    }

    public PipelineDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<PipelineDbContext>().UseNpgsql(ConnectionString).Options);

    /// <summary>
    /// Creates one API replica: its own service provider, DbContext factory and Postgres advisory-lock
    /// provider, wired the way the API host wires them.
    /// </summary>
    public DatabaseStartupService CreateReplica()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PipelineDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddDistributedLockProvider(ConnectionString);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);

        return new DatabaseStartupService(
            provider.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            provider.GetRequiredService<IDistributedLockProvider>(),
            new ConfigurationBuilder().Build(),
            Serilog.Core.Logger.None);
    }

    /// <summary>
    /// Removes every row that seeding writes or repairs, then adds one template whose project doesn't exist.
    /// Returns the template's ID.
    /// </summary>
    public async Task<Guid> ResetToEmptyWithOrphanedTemplateAsync()
    {
        await using var db = CreateDbContext();
        await db.PipelineJobTemplates.ExecuteDeleteAsync();
        await db.ReviewerConfigs.ExecuteDeleteAsync();
        await db.Projects.ExecuteDeleteAsync();

        var orphan = new PipelineJobTemplateEntity { Id = Guid.NewGuid(), ProjectId = Guid.NewGuid(), Name = "orphan" };
        db.PipelineJobTemplates.Add(orphan);
        await db.SaveChangesAsync();
        return orphan.Id;
    }

    private string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
}
