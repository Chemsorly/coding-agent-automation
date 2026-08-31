using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.Persistence;

/// <summary>
/// Tests verifying that <see cref="WorkItemTransitionService"/> passes the caught
/// <see cref="DbUpdateConcurrencyException"/> as the first argument to <c>LogWarning</c>
/// when retries are exhausted, so structured logs include the full stack trace.
/// Covers issue #2202 Fix B.
/// </summary>
public class WorkItemTransitionServiceLoggingTests
{
    // ── Helpers ───────────────────────────────────────────────────────────────

    private static DbContextOptions<PipelineDbContext> CreateDbOptions()
        => new DbContextOptionsBuilder<PipelineDbContext>()
            .UseInMemoryDatabase($"TransitionSvc-Logging-{Guid.NewGuid()}")
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;

    private static async Task<WorkItemEntity> SeedWorkItemAsync(
        DbContextOptions<PipelineDbContext> opts,
        WorkItemStatus status = WorkItemStatus.Pending)
    {
        await using var ctx = new LogTestPipelineDbContext(opts);
        ctx.Database.EnsureCreated();
        var item = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            IssueIdentifier = $"org/repo#{Guid.NewGuid():N}",
            IssueProviderConfigId = "ip-log-1",
            Status = status,
            FailureReason = status == WorkItemStatus.Failed ? FailureReason.InfrastructureFailure : null,
            TaskType = WorkItemTaskType.Implementation,
            CreatedAt = DateTimeOffset.UtcNow
        };
        ctx.WorkItems.Add(item);
        await ctx.SaveChangesAsync();
        return item;
    }

    // ── TransitionAsync — exception passed to LogWarning ─────────────────────

    /// <summary>
    /// When TransitionAsync exhausts all retries due to DbUpdateConcurrencyException,
    /// the exception must be passed as the first argument to ILogger.LogWarning so
    /// structured logs capture the stack trace (Fix B, site 1).
    /// </summary>
    [Fact]
    public async Task WhenAllRetriesExhausted_TransitionAsync_LogsWarningWithException()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Pending);

        // Always throw on save — all retries will be consumed
        var factory = new AlwaysThrowDbContextFactory(opts);

        Exception? capturedEx = null;
        var mockLogger = new Mock<ILogger<WorkItemTransitionService>>();
        mockLogger
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((_, _, _, ex, _) =>
            {
                if (ex is not null) capturedEx = ex;
            });

        var svc = new WorkItemTransitionService(factory, mockLogger.Object);

        var result = await svc.TransitionAsync(item.Id, WorkItemStatus.Dispatched);

        result.Should().BeFalse("all retries were exhausted");
        capturedEx.Should().BeOfType<DbUpdateConcurrencyException>(
            "the caught DbUpdateConcurrencyException must be passed to LogWarning so the stack trace appears in structured logs");
    }

    // ── TryRecoverFromInfrastructureFailureAsync — exception passed to LogWarning ──

    /// <summary>
    /// When TryRecoverFromInfrastructureFailureAsync exhausts all retries due to
    /// DbUpdateConcurrencyException, the exception must be passed as the first argument
    /// to ILogger.LogWarning (Fix B, site 2).
    /// </summary>
    [Fact]
    public async Task WhenAllRetriesExhausted_TryRecoverFromInfrastructureFailure_LogsWarningWithException()
    {
        var opts = CreateDbOptions();
        var item = await SeedWorkItemAsync(opts, WorkItemStatus.Failed);

        var factory = new AlwaysThrowDbContextFactory(opts);

        Exception? capturedEx = null;
        var mockLogger = new Mock<ILogger<WorkItemTransitionService>>();
        mockLogger
            .Setup(l => l.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.IsAny<It.IsAnyType>(),
                It.IsAny<Exception?>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()))
            .Callback<LogLevel, EventId, object, Exception?, Delegate>((_, _, _, ex, _) =>
            {
                if (ex is not null) capturedEx = ex;
            });

        var svc = new WorkItemTransitionService(factory, mockLogger.Object);

        var result = await svc.TryRecoverFromInfrastructureFailureAsync(
            item.Id, WorkItemStatus.Succeeded);

        result.Should().BeFalse("all retries were exhausted");
        capturedEx.Should().BeOfType<DbUpdateConcurrencyException>(
            "the caught DbUpdateConcurrencyException must be passed to LogWarning so the stack trace appears in structured logs");
    }

    // ── Test infrastructure ───────────────────────────────────────────────────

    private class LogTestPipelineDbContext : PipelineDbContext
    {
        public LogTestPipelineDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            foreach (var et in modelBuilder.Model.GetEntityTypes())
            {
                var rv = et.FindProperty("RowVersion");
                if (rv != null)
                {
                    rv.IsConcurrencyToken = false;
                    rv.ValueGenerated = Microsoft.EntityFrameworkCore.Metadata.ValueGenerated.Never;
                }
                foreach (var idx in et.GetIndexes().Where(i => i.GetFilter() != null).ToList())
                    et.RemoveIndex(idx);
            }
        }
    }

    /// <summary>
    /// A context that always throws DbUpdateConcurrencyException on SaveChangesAsync.
    /// </summary>
    private sealed class AlwaysThrowDbContext : LogTestPipelineDbContext
    {
        public AlwaysThrowDbContext(DbContextOptions<PipelineDbContext> options) : base(options) { }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException("Simulated concurrency conflict");
    }

    private sealed class AlwaysThrowDbContextFactory : IDbContextFactory<PipelineDbContext>
    {
        private readonly DbContextOptions<PipelineDbContext> _opts;
        public AlwaysThrowDbContextFactory(DbContextOptions<PipelineDbContext> opts) => _opts = opts;
        public PipelineDbContext CreateDbContext() => new AlwaysThrowDbContext(_opts);
        public Task<PipelineDbContext> CreateDbContextAsync(CancellationToken ct = default)
            => Task.FromResult(CreateDbContext());
    }
}
