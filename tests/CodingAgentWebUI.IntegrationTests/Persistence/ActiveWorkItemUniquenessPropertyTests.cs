// Feature: 035a-postgres-work-queue
// Property 2: Active Work Item Uniqueness
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace CodingAgentWebUI.IntegrationTests.Persistence;

/// <summary>
/// Property-based test verifying that the UNIQUE partial index on WorkItems prevents
/// duplicate active work items for the same (IssueIdentifier, IssueProviderConfigId).
/// Terminal statuses (Succeeded, Failed, Cancelled) are excluded from the constraint.
/// **Validates: Requirements 1.10, 4.6**
/// </summary>
public class ActiveWorkItemUniquenessPropertyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder()
        .WithImage("postgres:15-alpine")
        .Build();

    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();

        // Apply schema via EnsureCreated (includes partial unique index)
        await using var ctx = CreateContext();
        await ctx.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
    }

    private PipelineDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PipelineDbContext>()
            .UseNpgsql(_connectionString)
            .Options;
        return new PipelineDbContext(options);
    }

    /// <summary>
    /// Property 2: Active Work Item Uniqueness — duplicate active insert violates constraint.
    /// For any (IssueIdentifier, IssueProviderConfigId) pair with a non-terminal first status,
    /// inserting a second row with any non-terminal status SHALL throw a unique constraint violation.
    /// **Validates: Requirements 1.10, 4.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ActiveWorkItemArbitraries) })]
    public async Task DuplicateActiveWorkItem_ViolatesUniqueConstraint(
        NonEmptyString issueIdentifier,
        NonEmptyString issueProviderConfigId,
        ActiveStatusPair statusPair)
    {
        await using var ctx = CreateContext();

        // Use unique suffix per iteration to avoid cross-iteration interference
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var issueId = $"{issueIdentifier.Get}_{suffix}";
        var configId = $"{issueProviderConfigId.Get}_{suffix}";

        // Insert first work item with non-terminal status
        var first = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueId,
            IssueProviderConfigId = configId,
            Status = statusPair.FirstStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        };
        ctx.WorkItems.Add(first);
        await ctx.SaveChangesAsync();

        // Attempt second insert with same identifiers and non-terminal status
        var second = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueId,
            IssueProviderConfigId = configId,
            Status = statusPair.SecondStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        };
        ctx.WorkItems.Add(second);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => ctx.SaveChangesAsync());

        // Postgres unique violation is SQLSTATE 23505
        Assert.Contains("23505", ex.InnerException?.Message ?? ex.Message);
    }

    /// <summary>
    /// Complementary property: inserting a work item with terminal status for the same identifier
    /// as an existing active work item SHALL succeed (partial index excludes terminal statuses).
    /// **Validates: Requirements 1.10, 4.6**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(ActiveWorkItemArbitraries) })]
    public async Task TerminalStatusInsert_DoesNotViolateConstraint(
        NonEmptyString issueIdentifier,
        NonEmptyString issueProviderConfigId,
        WorkItemStatus activeStatus,
        TerminalStatus terminalStatus)
    {
        // Only use non-terminal statuses for the first insert
        if (activeStatus is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
            return; // Skip — FsCheck will generate other values

        await using var ctx = CreateContext();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var issueId = $"{issueIdentifier.Get}_{suffix}";
        var configId = $"{issueProviderConfigId.Get}_{suffix}";

        // Insert active work item
        var active = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueId,
            IssueProviderConfigId = configId,
            Status = activeStatus,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        };
        ctx.WorkItems.Add(active);
        await ctx.SaveChangesAsync();

        // Insert work item with TERMINAL status — should succeed
        var terminal = new WorkItemEntity
        {
            Id = Guid.NewGuid(),
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = issueId,
            IssueProviderConfigId = configId,
            Status = terminalStatus.Value,
            CreatedAt = DateTimeOffset.UtcNow,
            TimeoutSeconds = 3600
        };
        ctx.WorkItems.Add(terminal);

        // Should NOT throw
        await ctx.SaveChangesAsync();

        // Verify both rows exist
        var count = await ctx.WorkItems
            .Where(w => w.IssueIdentifier == issueId && w.IssueProviderConfigId == configId)
            .CountAsync();
        Assert.Equal(2, count);
    }
}

/// <summary>
/// Wrapper to represent a pair of non-terminal statuses for property test generation.
/// </summary>
public record ActiveStatusPair(WorkItemStatus FirstStatus, WorkItemStatus SecondStatus);

/// <summary>
/// Wrapper to represent a terminal status value (Succeeded, Failed, Cancelled).
/// </summary>
public record TerminalStatus(WorkItemStatus Value);

/// <summary>
/// FsCheck arbitrary generators for Active Work Item Uniqueness property tests.
/// Generates non-terminal status pairs and terminal statuses.
/// </summary>
public class ActiveWorkItemArbitraries
{
    public static Arbitrary<ActiveStatusPair> ActiveStatusPairArb()
    {
        var gen =
            from first in Gen.Elements(WorkItemStatus.Pending, WorkItemStatus.Dispatched, WorkItemStatus.Running)
            from second in Gen.Elements(WorkItemStatus.Pending, WorkItemStatus.Dispatched, WorkItemStatus.Running)
            select new ActiveStatusPair(first, second);
        return gen.ToArbitrary();
    }

    public static Arbitrary<TerminalStatus> TerminalStatusArb()
    {
        var gen = Gen.Elements(WorkItemStatus.Succeeded, WorkItemStatus.Failed, WorkItemStatus.Cancelled)
            .Select(s => new TerminalStatus(s));
        return gen.ToArbitrary();
    }
}
