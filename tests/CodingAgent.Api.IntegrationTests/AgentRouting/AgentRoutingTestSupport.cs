using CodingAgent.Api.Dispatch;
using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Serilog.Core;
using Serilog.Events;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>An InMemory database, a capturing logger and an in-process lock for the spec 053 routing tests.</summary>
internal static class AgentRoutingTestSupport
{
    public static PipelineDbContext NewDb(string? name = null) => InMemoryFactory.Create(name ?? Guid.NewGuid().ToString());

    /// <summary>
    /// A <see cref="WorkItemRouter"/> over the routing table in <paramref name="dbFactory"/> (spec 053 task 4.0). With the
    /// defaults, every task type routes to <see cref="AgentRoutingTestData.Profile"/> on
    /// <see cref="AgentRoutingTestData.DefaultJobTemplate"/> once the table is seeded (<see cref="SeedRouting"/>).
    /// </summary>
    public static WorkItemRouter Router(
        IDbContextFactory<PipelineDbContext> dbFactory,
        IReadOnlyList<AgentProfile>? profiles = null,
        IReadOnlyList<PipelineJobTemplate>? templates = null,
        JobTemplateStore? jobTemplates = null)
    {
        var profileStore = new Mock<IAgentProfileStore>();
        profileStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(profiles ?? [AgentRoutingTestData.Profile()]);
        var projectStore = new Mock<IProjectStore>();
        projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates ?? []);
        return new WorkItemRouter(
            profileStore.Object, new AgentRoutingStore(dbFactory), projectStore.Object,
            jobTemplates ?? AgentRoutingTestData.JobTemplates(), NullLogger<WorkItemRouter>.Instance);
    }

    /// <summary>Stores <paramref name="routing"/> (by default <see cref="AgentRoutingTestData.Routing"/>) synchronously, for test constructors.</summary>
    public static void SeedRouting(IDbContextFactory<PipelineDbContext> dbFactory, RoutingTable? routing = null)
    {
        using var db = dbFactory.CreateDbContext();
        db.KeyValueStore.Add(new KeyValueEntity
        {
            Key = RoutingTable.StorageKey,
            Value = AgentRoutingStore.Serialize(routing ?? AgentRoutingTestData.Routing()),
        });
        db.SaveChanges();
    }

    /// <summary>A Serilog logger that adds every event to <paramref name="events"/>.</summary>
    public static Serilog.ILogger Logger(List<LogEvent> events) =>
        new Serilog.LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(new ListSink(events)).CreateLogger();

    private sealed class ListSink(List<LogEvent> events) : ILogEventSink
    {
        public void Emit(LogEvent logEvent)
        {
            lock (events)
                events.Add(logEvent);
        }
    }

    /// <summary>One named lock at a time, like the Postgres advisory lock (not reentrant).</summary>
    public sealed class SemaphoreLockProvider : IDistributedLockProvider
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public async Task<IAsyncDisposable> AcquireAsync(string lockName, CancellationToken ct = default)
        {
            await _gate.WaitAsync(ct);
            return new Release(_gate);
        }

        private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
        {
            public ValueTask DisposeAsync()
            {
                gate.Release();
                return ValueTask.CompletedTask;
            }
        }
    }

    public sealed class InMemoryFactory(string name) : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => Create(name);

        public static PipelineDbContext Create(string name) => new InMemoryPipelineDbContext(
            new DbContextOptionsBuilder<PipelineDbContext>()
                .UseInMemoryDatabase(name)
                .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
                .Options);
    }

    /// <summary>Disables the Postgres-only features InMemory cannot handle (the pattern of the other API tests).</summary>
    private sealed class InMemoryPipelineDbContext(DbContextOptions<PipelineDbContext> options) : PipelineDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                if (entityType.FindProperty("RowVersion") is { } rowVersion)
                {
                    rowVersion.IsConcurrencyToken = false;
                    rowVersion.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
                foreach (var index in entityType.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    entityType.RemoveIndex(index);
            }
        }
    }
}
