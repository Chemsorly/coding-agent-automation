using CodingAgent.Infrastructure.Locking;
using CodingAgent.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>An InMemory database, a capturing logger and an in-process lock for the spec 053 routing tests.</summary>
internal static class AgentRoutingTestSupport
{
    public static PipelineDbContext NewDb(string? name = null) => InMemoryFactory.Create(name ?? Guid.NewGuid().ToString());

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
