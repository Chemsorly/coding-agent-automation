using System.Collections;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Spec 053 req 7.1, 7.14 and task 3.7: the configuration move against one real PostgreSQL database. Two API replicas
/// start at once and only one moves; a move that fails inside a caller's transaction (a config import) leaves
/// nothing changed. The API tests cannot show either: their lock is in-process and EF InMemory has no transactions.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentRoutingMoveConcurrencyTests : IClassFixture<AgentRoutingPostgresFixture>
{
    private const int Rounds = 3;

    private readonly AgentRoutingPostgresFixture _fixture;

    public AgentRoutingMoveConcurrencyTests(AgentRoutingPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task RunWithLockAsync_TwoReplicasAtOnce_ExactlyOneMoves_AndNeitherFails_Req7_1()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");
        var scenario = AgentRoutingLegacyFixture.WithDefaultLabels;

        for (var round = 1; round <= Rounds; round++)
        {
            await _fixture.ResetAsync(scenario);
            var (factoryA, lockA) = _fixture.CreateReplica();
            var (factoryB, lockB) = _fixture.CreateReplica();

            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var moveA = Task.Run(async () =>
            {
                await start.Task;
                return await AgentRoutingMove.RunWithLockAsync(factoryA, lockA, scenario.ToolsByKey, Serilog.Core.Logger.None, CancellationToken.None);
            });
            var moveB = Task.Run(async () =>
            {
                await start.Task;
                return await AgentRoutingMove.RunWithLockAsync(factoryB, lockB, scenario.ToolsByKey, Serilog.Core.Logger.None, CancellationToken.None);
            });
            start.SetResult();

            var summaries = await Task.WhenAll(moveA, moveB);

            summaries.Count(s => s is not null).Should().Be(1, $"round {round}: exactly one replica moves");
            summaries.Single(s => s is not null)!.FullMove.Should().BeTrue();
            await using var db = _fixture.CreateDbContext();
            (await db.KeyValueStore.CountAsync(kv => kv.Key == AgentRouting.StorageKey)).Should().Be(1, $"round {round}");
            (await db.ProviderConfigs.CountAsync(p => p.Kind == ProviderKind.Agent)).Should().Be(0, $"round {round}");
        }
    }

    [Fact]
    public async Task RunAsync_FailingInsideTheCallersTransaction_RollsEverythingBack_Review3M4()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");
        var scenario = AgentRoutingLegacyFixture.WithDefaultLabels;
        await _fixture.ResetAsync(scenario);
        var before = await _fixture.SnapshotAsync();

        await using (var db = _fixture.CreateDbContext())
        {
            var strategy = db.Database.CreateExecutionStrategy();
            var act = () => strategy.ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync();
                // What an import does before the move: clear the configuration and write the bundle.
                await db.AgentProfiles.ExecuteDeleteAsync();
                await db.QualityGateConfigs.ExecuteDeleteAsync();
                await AgentRoutingMove.RunAsync(db, new ThrowingToolsByKey(), Serilog.Core.Logger.None, CancellationToken.None);
                await transaction.CommitAsync();
            });

            await act.Should().ThrowAsync<InvalidOperationException>();
        }

        (await _fixture.SnapshotAsync()).Should().Equal(before, "the failed move rolled the whole transaction back");
    }

    /// <summary>Throws on first use, after the move has read the configuration.</summary>
    private sealed class ThrowingToolsByKey : IReadOnlyDictionary<string, string>
    {
        private static InvalidOperationException Fail() => new("job templates unavailable");

        public string this[string key] => throw Fail();
        public IEnumerable<string> Keys => throw Fail();
        public IEnumerable<string> Values => throw Fail();
        public int Count => throw Fail();
        public bool ContainsKey(string key) => throw Fail();
        public bool TryGetValue(string key, [MaybeNullWhen(false)] out string value) => throw Fail();
        public IEnumerator<KeyValuePair<string, string>> GetEnumerator() => throw Fail();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

/// <summary>One PostgreSQL container per test class, with all migrations applied.</summary>
public sealed class AgentRoutingPostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private readonly List<ServiceProvider> _providers = [];

    public Exception? InitializationException { get; private set; }

    public bool IsDockerUnavailable => _container is null;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:17-alpine")
                .WithDatabase("routing_move_test")
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

    /// <summary>One API replica's DbContext factory and Postgres advisory-lock provider, wired as the API wires them.</summary>
    public (IDbContextFactory<PipelineDbContext> Factory, IDistributedLockProvider Lock) CreateReplica()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<PipelineDbContext>(options => options.UseNpgsql(ConnectionString));
        services.AddDistributedLockProvider(ConnectionString);
        var provider = services.BuildServiceProvider();
        _providers.Add(provider);
        return (provider.GetRequiredService<IDbContextFactory<PipelineDbContext>>(), provider.GetRequiredService<IDistributedLockProvider>());
    }

    /// <summary>Empties the configuration tables and seeds <paramref name="scenario"/>.</summary>
    public async Task ResetAsync(LegacyRoutingScenario scenario)
    {
        await using var db = CreateDbContext();
        await db.AgentProfiles.ExecuteDeleteAsync();
        await db.ProviderConfigs.ExecuteDeleteAsync();
        await db.PipelineJobTemplates.ExecuteDeleteAsync();
        await db.QualityGateConfigs.ExecuteDeleteAsync();
        await db.ReviewerConfigs.ExecuteDeleteAsync();
        await db.PipelineConfig.ExecuteDeleteAsync();
        await db.KeyValueStore.ExecuteDeleteAsync();
        await db.Projects.ExecuteDeleteAsync();
        await AgentRoutingLegacyFixture.SeedAsync(db, scenario);
    }

    /// <summary>Every configuration row the move reads or writes, in a stable order.</summary>
    public async Task<List<string?>> SnapshotAsync()
    {
        await using var db = CreateDbContext();
        return
        [
            .. await db.AgentProfiles.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
            .. await db.ProviderConfigs.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
            .. await db.PipelineJobTemplates.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
            .. await db.QualityGateConfigs.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
            .. await db.KeyValueStore.AsNoTracking().OrderBy(e => e.Key).Select(e => e.Value).ToListAsync(),
            .. await db.PipelineConfig.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Configuration).ToListAsync(),
        ];
    }

    private string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
}
