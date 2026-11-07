using AwesomeAssertions;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace CodingAgent.Infrastructure.IntegrationTests.Persistence;

/// <summary>
/// Spec 056 DD5, req 2.3, 2.5 (task 2.1): the pause store on two API replicas against one PostgreSQL database, each
/// with its own advisory-lock provider. The API tests cannot show this: their lock is in-process.
/// </summary>
[Trait("Category", "Integration")]
public sealed class CredentialPauseStoreConcurrencyTests : IClassFixture<WorkItemPostgresFixture>
{
    private const int Rounds = 3;
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 6, 26, 0, TimeSpan.Zero);

    private readonly WorkItemPostgresFixture _fixture;

    public CredentialPauseStoreConcurrencyTests(WorkItemPostgresFixture fixture)
    {
        _fixture = fixture;
    }

    private CredentialPauseStore Replica(TimeProvider time)
    {
        var (factory, locks) = _fixture.CreateReplica();
        return new CredentialPauseStore(factory, locks, time, Serilog.Core.Logger.None);
    }

    private static AgentCredentialPause Auto(string credential, DateTimeOffset? until) => new(
        credential, CredentialPauseReasons.QuotaExhausted, CredentialPauseSources.Auto, Now, until, "quota");

    private static async Task<T[]> AtOnce<T>(params Func<Task<T>>[] actions)
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = actions.Select(a => Task.Run(async () =>
        {
            await start.Task;
            return await a();
        })).ToArray();
        start.SetResult();
        return await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task TwoAutoPausesOfOneCredential_AtOnce_KeepOneEntryWithTheLaterUntil_Req2_5()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");

        for (var round = 1; round <= Rounds; round++)
        {
            await _fixture.ResetAsync();
            var time = new SettableTime(Now);
            var a = Replica(time);
            var b = Replica(time);

            await AtOnce(
                () => a.PauseAsync(Auto(AgentCredentials.Kiro, Now.AddDays(1)), CancellationToken.None),
                () => b.PauseAsync(Auto(AgentCredentials.Kiro, Now.AddDays(5)), CancellationToken.None));

            await using var db = _fixture.CreateDbContext();
            (await db.KeyValueStore.CountAsync(kv => kv.Key == CredentialPauseStore.KeyOf(AgentCredentials.Kiro)))
                .Should().Be(1, $"round {round}");
            (await a.LoadAsync(AgentCredentials.Kiro, CancellationToken.None))!.Until
                .Should().Be(Now.AddDays(5), $"round {round}: the later end time wins whichever write came first");
        }
    }

    [Fact]
    public async Task PausesOfTwoCredentials_AtOnce_AreBothStored_DD5()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");

        for (var round = 1; round <= Rounds; round++)
        {
            await _fixture.ResetAsync();
            var time = new SettableTime(Now);
            var a = Replica(time);
            var b = Replica(time);

            await AtOnce(
                () => a.PauseAsync(Auto(AgentCredentials.Kiro, null), CancellationToken.None),
                () => b.PauseAsync(Auto(AgentCredentials.Opencode, null), CancellationToken.None));

            (await a.LoadAllAsync(CancellationToken.None)).Select(p => p.Credential)
                .Should().BeEquivalentTo([AgentCredentials.Kiro, AgentCredentials.Opencode], $"round {round}");
        }
    }

    [Fact]
    public async Task AnExpiredPause_ReadByTwoReplicasAtOnce_IsResumedOnce_Req2_3()
    {
        if (_fixture.IsDockerUnavailable) return;
        _fixture.InitializationException.Should().BeNull("the container must start and all migrations must apply");

        for (var round = 1; round <= Rounds; round++)
        {
            await _fixture.ResetAsync();
            var time = new SettableTime(Now);
            var a = Replica(time);
            var b = Replica(time);
            await a.PauseAsync(Auto(AgentCredentials.Kiro, Now.AddMinutes(1)), CancellationToken.None);
            time.Now = Now.AddMinutes(2);

            var expired = await AtOnce(
                () => a.ExpireAsync(AgentCredentials.Kiro, CancellationToken.None),
                () => b.ExpireAsync(AgentCredentials.Kiro, CancellationToken.None));

            expired.Count(e => e).Should().Be(1, $"round {round}: exactly one replica records the resume");
            (await a.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
        }
    }

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}

/// <summary>
/// One PostgreSQL container per test class, with all migrations applied, for the work item and key-value tables
/// (spec 056).
/// </summary>
public sealed class WorkItemPostgresFixture : IAsyncLifetime
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
                .WithDatabase("work_items_test")
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

    /// <summary>Empties the work item and key-value tables.</summary>
    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.WorkItems.ExecuteDeleteAsync();
        await db.KeyValueStore.ExecuteDeleteAsync();
    }

    private string ConnectionString =>
        _container?.GetConnectionString()
        ?? throw new InvalidOperationException("The PostgreSQL container is not available.");
}
