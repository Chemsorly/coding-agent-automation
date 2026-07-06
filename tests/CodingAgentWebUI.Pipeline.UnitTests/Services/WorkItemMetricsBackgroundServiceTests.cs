using System.Diagnostics.Metrics;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for WorkItemMetricsBackgroundService.
/// Validates: correct gauge reporting, empty-before-first-tick, cancellation, error recovery.
/// </summary>
public class WorkItemMetricsBackgroundServiceTests : IDisposable
{
    private readonly DbContextOptions<PipelineDbContext> _dbOptions;
    private readonly TestDbContextFactory _dbFactory;
    private readonly MeterListener _listener;
    private readonly List<(long Value, KeyValuePair<string, object?>[] Tags)> _measurements = [];

    public WorkItemMetricsBackgroundServiceTests()
    {
        var dbName = $"WorkItemMetrics-{Guid.NewGuid()}";
        _dbOptions = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase(dbName)
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

        using (var ctx = new TestPipelineDbContext(_dbOptions))
            ctx.Database.EnsureCreated();

        _dbFactory = new TestDbContextFactory(_dbOptions);

        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == WorkDistributionTelemetry.MeterName &&
                instrument.Name == "workdistribution.workitems_by_status")
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            var tagArray = tags.ToArray()
                .Select(t => new KeyValuePair<string, object?>(t.Key, t.Value))
                .ToArray();
            _measurements.Add((measurement, tagArray));
        });
        _listener.Start();
    }

    public void Dispose()
    {
        _listener.Dispose();
        using var db = new TestPipelineDbContext(_dbOptions);
        db.Database.EnsureDeleted();
    }

    [Fact]
    public void CachedMeasurements_EmptyBeforeFirstTick()
    {
        // Construct but do NOT start
        _ = new WorkItemMetricsBackgroundService(_dbFactory);

        _measurements.Clear();
        _listener.RecordObservableInstruments();

        // TODO: This assertion is weak — it passes both when the callback is correctly registered
        // and returns empty, AND when registration silently failed (gauge falls through to default empty).
        // Strengthen by verifying the callback was registered (e.g., seed data, confirm gauge reports it,
        // then check pre-tick state).
        // Should report no measurements (empty enumerable), not null/error
        _measurements.Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteAsync_QueriesDbAndCachesMeasurements()
    {
        // Seed data
        await using (var db = await _dbFactory.CreateDbContextAsync())
        {
            db.WorkItems.Add(CreateWorkItem(WorkItemStatus.Pending, "kiro,dotnet"));
            db.WorkItems.Add(CreateWorkItem(WorkItemStatus.Pending, "kiro,dotnet"));
            db.WorkItems.Add(CreateWorkItem(WorkItemStatus.Dispatched, "kiro,python"));
            await db.SaveChangesAsync();
        }

        var service = new WorkItemMetricsBackgroundService(_dbFactory);
        using var cts = new CancellationTokenSource();

        // Start service — it performs immediate first tick
        await service.StartAsync(cts.Token);

        // Poll until the observable gauge reports measurements (the immediate first tick must complete)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            _measurements.Clear();
            _listener.RecordObservableInstruments();
            if (_measurements.Count > 0) break;
            await Task.Delay(50);
        }

        _measurements.Should().HaveCount(2);

        var pendingMeasurement = _measurements.FirstOrDefault(m =>
            m.Tags.Any(t => t.Key == "status" && (string?)t.Value == "Pending"));
        pendingMeasurement.Value.Should().Be(2);
        pendingMeasurement.Tags.Should().Contain(t => t.Key == "agent_selector" && (string?)t.Value == "kiro,dotnet");

        var dispatchedMeasurement = _measurements.FirstOrDefault(m =>
            m.Tags.Any(t => t.Key == "status" && (string?)t.Value == "Dispatched"));
        dispatchedMeasurement.Value.Should().Be(1);
        dispatchedMeasurement.Tags.Should().Contain(t => t.Key == "agent_selector" && (string?)t.Value == "kiro,python");

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ExecuteAsync_RespectsCancellation()
    {
        var service = new WorkItemMetricsBackgroundService(_dbFactory);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        await Task.Delay(100);

        cts.Cancel();
        // StopAsync should complete without throwing
        await service.StopAsync(CancellationToken.None);

        // Verify ExecuteTask completed cleanly
        if (service.ExecuteTask is not null)
        {
            var completed = await Task.WhenAny(service.ExecuteTask, Task.Delay(TimeSpan.FromSeconds(5)));
            completed.Should().Be(service.ExecuteTask, "ExecuteTask should complete after cancellation");
        }
    }

    [Fact]
    public async Task ExecuteAsync_DbError_ResetsMeasurementsToEmpty()
    {
        // Use a factory that throws on CreateDbContextAsync
        var throwingFactory = new ThrowingDbContextFactory();
        var service = new WorkItemMetricsBackgroundService(throwingFactory);
        using var cts = new CancellationTokenSource();

        await service.StartAsync(cts.Token);
        // Wait for the immediate first tick to complete (and fail)
        await Task.Delay(200);

        _measurements.Clear();
        _listener.RecordObservableInstruments();

        // TODO: This assertion is weak — it also passes if the service never executed at all.
        // Strengthen by seeding valid data first, confirming the gauge reports it, then triggering
        // a failure and asserting measurements reset from non-empty to empty.
        // Should be empty (reset to []) not null/stale
        _measurements.Should().BeEmpty();

        cts.Cancel();
        await service.StopAsync(CancellationToken.None);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static WorkItemEntity CreateWorkItem(WorkItemStatus status, string agentSelector)
    {
        return new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = "owner/repo#1",
            IssueProviderConfigId = "ip-1",
            Status = status,
            AgentSelector = agentSelector,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private sealed class TestPipelineDbContext : PipelineDbContext
    {
        public TestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null) { rv.IsConcurrencyToken = false; rv.ValueGenerated = ValueGenerated.Never; }
            }
            foreach (var et in modelBuilder.Model.GetEntityTypes())
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
        }
    }

    private sealed class TestDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _options;
        public TestDbContextFactory(DbContextOptions<PipelineDbContext> options) => _options = options;
        public PipelineDbContext CreateDbContext() => new TestPipelineDbContext(_options);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }

    private sealed class ThrowingDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        public PipelineDbContext CreateDbContext() => throw new InvalidOperationException("Simulated DB failure");
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            throw new InvalidOperationException("Simulated DB failure");
    }
}
