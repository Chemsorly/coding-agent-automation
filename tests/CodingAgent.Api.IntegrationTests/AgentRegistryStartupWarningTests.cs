using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Serializes all tests in this class to prevent concurrent <c>Log.Logger</c> mutations
/// from interfering with each other or with <see cref="PostStatusIdempotencyCollection"/>.
/// </summary>
[CollectionDefinition("AgentRegistryStartupWarningCollection", DisableParallelization = true)]
public sealed class AgentRegistryStartupWarningCollection { }

/// <summary>
/// Tests for the startup warning emitted when <see cref="IAgentRegistryService"/> is
/// configured in in-memory mode with more than one API replica (split-brain risk).
///
/// Issue #2645: No startup warning when api.replicas &gt; 1 without Redis.
///
/// Tests 1 and 2 build a minimal <see cref="ServiceCollection"/> with only the services
/// required by <see cref="ApiServiceCollectionExtensions.CreateAgentRegistryService"/>
/// (<see cref="AgentRegistryService"/>; no <c>IConnectionMultiplexer</c>), install a
/// capturing <c>Log.Logger</c>, and call the real production factory method directly.
/// This avoids the <c>UseSerilog</c> overwrite that occurs with full
/// <see cref="WebApplicationFactory{T}"/> hosts while still exercising the production
/// code path rather than an inline copy.
///
/// Test 3 uses a standalone <see cref="WebApplicationFactory{TEntryPoint}"/> because it
/// tests the fail-fast throw path that is only meaningful during full host startup.
/// </summary>
[Collection("AgentRegistryStartupWarningCollection")]
public sealed class AgentRegistryStartupWarningTests
{
    // ─── Helper: flatten exception chain for startup-failure assertions ───────

    // TODO [WARNING]: FlattenMessages walks InnerException linearly. If the host wraps the
    // thrown InvalidOperationException inside an AggregateException with multiple InnerExceptions
    // (e.g. when HostBuilder collects multiple startup failures), only the first InnerException
    // chain is walked and the relevant message may be missed, causing a false-negative assertion
    // failure. To handle this, recursively flatten AggregateException.InnerExceptions in addition
    // to InnerException (e.g. via Exception.Flatten() or a queue-based walk).
    // See review finding [WARNING] AgentRegistryStartupWarningTests.cs:71 (Correctness review).
    private static string FlattenMessages(Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        for (var e = ex; e is not null; e = e.InnerException)
            sb.AppendLine(e.Message);
        return sb.ToString();
    }

    // ─── Helper: build a minimal IConfiguration ───────────────────────────────

    private static IConfiguration BuildConfig(int replicaCount = 1, bool failOnMultiReplica = false)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:ChatReplicaCount"] = replicaCount.ToString(),
                ["Api:FailOnMultiReplicaWithoutRedis"] = failOnMultiReplica.ToString()
            })
            .Build();

    /// <summary>
    /// Builds a minimal <see cref="ServiceProvider"/> containing only the services required
    /// by <see cref="ApiServiceCollectionExtensions.CreateAgentRegistryService"/>:
    /// <see cref="AgentRegistryService"/> and <c>Serilog.ILogger</c>.
    /// No <c>IConnectionMultiplexer</c> is registered, so the factory takes the in-memory path.
    /// </summary>
    private static ServiceProvider BuildMinimalProvider()
    {
        var services = new ServiceCollection();
        // AgentRegistryService requires Serilog.ILogger injected by DI.
        // Use the current static Log.Logger so events flow to the capturing sink.
        services.AddSingleton<Serilog.ILogger>(_ => Log.Logger);
        services.AddSingleton<AgentRegistryService>();
        return services.BuildServiceProvider();
    }

    // ─── Test 1: replicaCount > 1, no Redis → Warning emitted ────────────────

    /// <summary>
    /// When the API starts with no Redis and ChatReplicaCount = 2 (injected via
    /// <c>WorkDistribution__Dispatch__ChatReplicaCount</c>), at least one Warning-level log
    /// entry must be emitted containing "split-brain" and the service must resolve to the
    /// in-memory <see cref="AgentRegistryService"/> (not the distributed variant).
    /// Acceptance criterion 1 of issue #2645.
    ///
    /// Calls the real <see cref="ApiServiceCollectionExtensions.CreateAgentRegistryService"/>
    /// production method to ensure the test fails if the production logic is removed.
    /// </summary>
    [Fact]
    public void AddApiOrchestration_WithNoRedisAndReplicaCount2_EmitsWarning()
    {
        var sink = new RegistryWarnCapturingSink();
        var previousLogger = Log.Logger;

        try
        {
            // Route static Log.Warning to the capturing sink before invoking the factory.
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink)
                .CreateLogger();

            var config = BuildConfig(replicaCount: 2);

            using var provider = BuildMinimalProvider();

            // Call the real production factory method — not an inline copy.
            var registry = ApiServiceCollectionExtensions.CreateAgentRegistryService(provider, config);

            // The registry must be the in-memory variant (no Redis)
            registry.Should().BeOfType<AgentRegistryService>(
                "without Redis the in-memory AgentRegistryService must be returned");

            // At least one Warning-level event must mention split-brain
            var warnings = sink.Events
                .Where(e => e.Level == LogEventLevel.Warning)
                .ToList();
            warnings.Should().NotBeEmpty(
                "a Warning must be emitted when in-memory mode is used with replicaCount > 1");
            warnings.Should().Contain(
                e => e.RenderMessage().Contains("split-brain"),
                "the warning message must mention split-brain");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // ─── Test 2: replicaCount = 1 (default), no Redis → no Warning ───────────

    /// <summary>
    /// When the API starts with no Redis and ChatReplicaCount = 1 (the default — no override),
    /// no Warning-level log entry about split-brain must be emitted.
    /// The existing <c>Information</c> log is unchanged.
    /// Acceptance criterion 2 of issue #2645.
    ///
    /// Calls the real <see cref="ApiServiceCollectionExtensions.CreateAgentRegistryService"/>
    /// production method to ensure the test fails if the production logic is removed.
    /// </summary>
    [Fact]
    public void AddApiOrchestration_WithNoRedisAndDefaultReplicaCount_EmitsNoWarning()
    {
        var sink = new RegistryWarnCapturingSink();
        var previousLogger = Log.Logger;

        try
        {
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Verbose()
                .WriteTo.Sink(sink)
                .CreateLogger();

            var config = BuildConfig(replicaCount: 1);

            using var provider = BuildMinimalProvider();

            // Call the real production factory method — not an inline copy.
            _ = ApiServiceCollectionExtensions.CreateAgentRegistryService(provider, config);

            var splitBrainWarnings = sink.Events
                .Where(e => e.Level == LogEventLevel.Warning
                         && e.RenderMessage().Contains("split-brain"))
                .ToList();
            splitBrainWarnings.Should().BeEmpty(
                "no split-brain Warning must be emitted when running in single-replica mode without Redis");
        }
        finally
        {
            Log.Logger = previousLogger;
        }
    }

    // ─── Test 3: replicaCount > 1, FailOnMultiReplicaWithoutRedis=true → throws ─

    /// <summary>
    /// When <c>Api:FailOnMultiReplicaWithoutRedis</c> is <c>true</c> and ChatReplicaCount = 2,
    /// host startup must throw with an <see cref="InvalidOperationException"/> message that
    /// identifies the split-brain condition.
    /// Acceptance criterion 3 of issue #2645.
    /// Uses a full <see cref="WebApplicationFactory{TEntryPoint}"/> to verify the throw
    /// propagates through the real host startup path.
    /// </summary>
    [Fact]
    public void AddApiOrchestration_WithNoRedisAndReplicaCount2AndFailFlag_ThrowsOnStartup()
    {
        using var factory = new AgentRegistryFailFastFactory();

        // CreateClient triggers host startup → RegisterApiObservableGauges →
        // GetRequiredService<IAgentRegistryService>() → factory lambda → throw
        var ex = Record.Exception(() => factory.CreateClient());

        ex.Should().NotBeNull(
            "startup must fail when FailOnMultiReplicaWithoutRedis=true and replicaCount > 1");

        var allMessages = FlattenMessages(ex!);
        allMessages.Should().Contain("multiple replicas",
            "the exception chain must mention multiple replicas");
        // TODO [WARNING]: The assertion above checks only that the string "multiple replicas" appears
        // somewhere in the flattened exception chain. It does not assert the exception type
        // (InvalidOperationException). A future change that throws a different exception type with a
        // message containing the same substring would pass this test accidentally. Consider adding:
        //   ex.Should().BeOfType<InvalidOperationException>()
        //   or walking the chain for InvalidOperationException specifically.
        // See review finding [WARNING] AgentRegistryStartupWarningTests.cs:259 (TestQualityReviewer).
        // TODO [WARNING]: There is no test for the case where Redis IS configured (IConnectionMultiplexer
        // registered) alongside ChatReplicaCount > 1. The production code takes the early-return
        // distributed path and must emit no split-brain warning. Without this test, a regression that
        // moves the replica-count check before the Redis check would go undetected.
        // See review finding [WARNING] AgentRegistryStartupWarningTests.cs:1 (TestQualityReviewer).
    }
}

// ─── Capturing sink (scoped to this feature) ─────────────────────────────────

/// <summary>
/// Thread-safe Serilog sink that stores all emitted events for test assertion.
/// Used to intercept <c>Log.Warning</c> calls on the static <c>Log.Logger</c>.
/// </summary>
internal sealed class RegistryWarnCapturingSink : ILogEventSink
{
    private readonly System.Collections.Concurrent.ConcurrentBag<LogEvent> _events = new();

    public IReadOnlyCollection<LogEvent> Events => _events;

    public void Emit(LogEvent logEvent) => _events.Add(logEvent);
}

// ─── Shared factory helpers ───────────────────────────────────────────────────

/// <summary>
/// Shared DI configuration for <see cref="AgentRegistryFailFastFactory"/>.
/// Replaces real infrastructure (Postgres, K8s) with in-memory / mock equivalents.
/// </summary>
internal static class AgentRegistryTestFactoryHelpers
{
    internal static void ConfigureTestServices(IServiceCollection services)
    {
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(5));
        services.RemoveAll<IHostedService>();

        RemoveDbContextRegistrations(services);
        services.AddSingleton<IDbContextFactory<PipelineDbContext>>(
            new RegistryWarnDbContextFactory($"AgentRegistryWarn-{Guid.NewGuid():N}"));

        services.RemoveAll<IDistributedLockProvider>();
        services.AddDistributedLockProvider(null);

        services.RemoveAll<IDatabaseProbe>();
        services.AddSingleton<IDatabaseProbe>(new RegistryWarnNoOpDatabaseProbe());

        services.RemoveAll<IProviderFactory>();
        services.AddSingleton(new Mock<IProviderFactory>().Object);

        services.RemoveAll<IQualityGateValidator>();
        services.AddSingleton(new Mock<IQualityGateValidator>().Object);

        services.RemoveAll<ILeaderElectionService>();
        var leaderMock = new Mock<ILeaderElectionService>();
        leaderMock.SetupGet(l => l.IsLeader).Returns(true);
        leaderMock.SetupGet(l => l.LeaderToken).Returns(CancellationToken.None);
        services.AddSingleton(leaderMock.Object);

        services.RemoveAll<AssignmentEnricher>();
        services.AddSingleton<AssignmentEnricher>(new RegistryWarnPassthroughEnricher());

        services.RemoveAll<IKubernetesJobClient>();
        services.AddSingleton(new Mock<IKubernetesJobClient>().Object);
    }

    internal static void RemoveDbContextRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(d => d.ServiceType == typeof(IDbContextFactory<PipelineDbContext>)
                     || d.ServiceType == typeof(PipelineDbContext)
                     || d.ServiceType == typeof(DbContextOptions<PipelineDbContext>)
                     || d.ServiceType == typeof(DbContextOptions)
                     || d.ServiceType.Name.Contains("DbContextPool"))
            .ToList();
        foreach (var d in toRemove) services.Remove(d);
    }
}

/// <summary>
/// Standalone <see cref="WebApplicationFactory{TEntryPoint}"/> for the
/// <c>FailOnMultiReplicaWithoutRedis=true</c> throw path (Test 3).
/// Follows the <see cref="AgentHubGateKestrelFactory"/> pattern for standalone factories.
/// </summary>
internal sealed class AgentRegistryFailFastFactory : WebApplicationFactory<Program>
{
    public AgentRegistryFailFastFactory()
    {
        // Reset global logger to suppress noisy output from the failing factory host.
        // TODO [WARNING]: Log.Logger is mutated here but never restored in Dispose. Tests 1 and 2
        // save/restore Log.Logger correctly via a finally block. This factory does not follow the
        // same pattern: if this factory outlives the test (e.g., a later test in the process runs
        // after Dispose), the static logger remains at MinimumLevel.Warning (bootstrap level) rather
        // than whatever was configured before. Add a _previousLogger field and restore it in
        // Dispose(bool disposing) to match the save/restore contract. See review finding [WARNING]
        // AgentRegistryStartupWarningTests.cs:315 (TestQualityReviewer and Correctness review).
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .CreateBootstrapLogger();

        Environment.SetEnvironmentVariable("Database__Host", "localhost");
        Environment.SetEnvironmentVariable("Database__Port", "5432");
        Environment.SetEnvironmentVariable("Database__Username", "test");
        Environment.SetEnvironmentVariable("Database__Password", "test");
        Environment.SetEnvironmentVariable("Database__Name", "test_db");
        Environment.SetEnvironmentVariable("Database__SslMode", "Disable");
        Environment.SetEnvironmentVariable("Database__MigrateOnStartup", "false");
        Environment.SetEnvironmentVariable("Database__SkipStartupInit", "true");
        Environment.SetEnvironmentVariable("AGENT_API_KEY", ApiWebApplicationFactory.ApiKey);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration(cfg =>
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkDistribution:Dispatch:ChatReplicaCount"] = "2",
                ["Api:FailOnMultiReplicaWithoutRedis"] = "true"
            }));

        builder.ConfigureServices(AgentRegistryTestFactoryHelpers.ConfigureTestServices);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Environment.SetEnvironmentVariable("Database__Host", null);
            Environment.SetEnvironmentVariable("Database__Port", null);
            Environment.SetEnvironmentVariable("Database__Username", null);
            Environment.SetEnvironmentVariable("Database__Password", null);
            Environment.SetEnvironmentVariable("Database__Name", null);
            Environment.SetEnvironmentVariable("Database__SslMode", null);
            Environment.SetEnvironmentVariable("Database__MigrateOnStartup", null);
            Environment.SetEnvironmentVariable("Database__SkipStartupInit", null);
            Environment.SetEnvironmentVariable("AGENT_API_KEY", null);
        }
        base.Dispose(disposing);
    }
}

// ─── EF InMemory infrastructure ───────────────────────────────────────────────

internal sealed class RegistryWarnDbContextFactory : IDbContextFactory<PipelineDbContext>
{
    private readonly string _dbName;
    public RegistryWarnDbContextFactory(string dbName) => _dbName = dbName;

    public PipelineDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(_dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new RegistryWarnPipelineDbContext(options);
    }

    public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
        => Task.FromResult(CreateDbContext());
}

internal sealed class RegistryWarnPipelineDbContext : PipelineDbContext
{
    public RegistryWarnPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

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
        }
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            var indexesToRemove = entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList();
            foreach (var index in indexesToRemove)
                entityType.RemoveIndex(index);
        }
    }
}

internal sealed class RegistryWarnNoOpDatabaseProbe : IDatabaseProbe
{
    public Task ProbeAsync(CancellationToken ct) => Task.CompletedTask;
}

internal sealed class RegistryWarnPassthroughEnricher : AssignmentEnricher
{
    public RegistryWarnPassthroughEnricher() : base(Serilog.Log.Logger) { }

    public override Task<JobDistributionRequest?> EnrichAsync(
        JobDistributionRequest identity, PipelineProject project, CancellationToken ct)
        => Task.FromResult<JobDistributionRequest?>(identity);
}
