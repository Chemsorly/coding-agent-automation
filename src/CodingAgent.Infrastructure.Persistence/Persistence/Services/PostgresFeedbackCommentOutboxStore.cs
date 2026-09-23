using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed implementation of <see cref="IFeedbackCommentOutbox"/>.
/// Uses a context-per-operation pattern via <see cref="IDbContextFactory{TContext}"/>
/// (same pattern as <see cref="EfKeyValueStore"/>).
/// Registered as <c>AddSingleton&lt;IFeedbackCommentOutbox, PostgresFeedbackCommentOutboxStore&gt;()</c>.
/// </summary>
/// <remarks>
/// Known test coverage gap: the relay tests use a mocked IPipelineApiFeedbackCommentOutboxClient
/// and never exercise the store logic directly. Behaviours lacking store-level tests include:
/// MarkFailedAsync AttemptCount increment and Pending→Failed transition at maxAttempts,
/// GetPendingAsync Status=Pending + AttemptCount&lt;maxAttempts filter, and
/// EnqueueAsync RunId idempotency. EfKeyValueStoreTests shows the InMemory-backed repo pattern;
/// the RunId idempotency case requires SQLite or DbUpdateException simulation.
/// </remarks>
public sealed class PostgresFeedbackCommentOutboxStore : IFeedbackCommentOutbox
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public PostgresFeedbackCommentOutboxStore(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc/>
    public async Task EnqueueAsync(FeedbackCommentOutboxEntry entry, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = new FeedbackCommentOutboxEntity
        {
            Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id,
            RunId = entry.RunId,
            IssueProviderConfigId = entry.IssueProviderConfigId,
            IssueIdentifier = entry.IssueIdentifier,
            RepoProviderConfigId = entry.RepoProviderConfigId,
            PullRequestNumber = entry.PullRequestNumber,
            FeedbackJson = entry.FeedbackJson,
            Status = "Pending",
            AttemptCount = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };

        // ON CONFLICT DO NOTHING via explicit duplicate check: EF Core does not have a native
        // upsert-ignore API, so we check existence first and skip if the row already exists.
        // The unique index on RunId is the database-level guard against true concurrent duplicates.
        var exists = await db.FeedbackCommentOutbox
            .AnyAsync(f => f.RunId == entry.RunId, ct);

        if (!exists)
        {
            db.FeedbackCommentOutbox.Add(entity);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Unique index violation: another replica inserted the same RunId concurrently.
                // Treat as a no-op — the row is already in the outbox.
            }
            // All other DbUpdateException variants (connection loss, disk full, schema mismatch)
            // are intentionally NOT caught here so the caller sees the failure and can log+continue.
        }
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FeedbackCommentOutboxEntry>> GetPendingAsync(
        int maxAttempts,
        int pageSize,
        CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entities = await db.FeedbackCommentOutbox
            .AsNoTracking()
            .Where(f => f.Status == "Pending" && f.AttemptCount < maxAttempts)
            .OrderBy(f => f.CreatedAt)
            .Take(pageSize)
            .ToListAsync(ct);

        return entities.Select(ToEntry).ToList();
    }

    /// <inheritdoc/>
    public async Task MarkCompletedAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.FeedbackCommentOutbox.FindAsync([id], ct);
        if (entity is null) return;

        entity.Status = "Completed";
        entity.CompletedAt = DateTimeOffset.UtcNow;

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The xmin concurrency token means a concurrent mark-completed call (possible in the
            // at-least-once relay window) will throw DbUpdateConcurrencyException on the second
            // writer. The first writer already applied the Completed state, so treat this as a no-op.
        }
    }

    /// <inheritdoc/>
    public async Task MarkFailedAsync(Guid id, string errorMessage, int maxAttempts, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.FeedbackCommentOutbox.FindAsync([id], ct);
        if (entity is null) return;

        entity.AttemptCount += 1;
        entity.LastAttemptAt = DateTimeOffset.UtcNow;
        entity.ErrorMessage = errorMessage;

        // Transition to Failed once all retries are exhausted so the relay's
        // GetPendingAsync query (Status=Pending, AttemptCount < maxAttempts) stops returning it.
        entity.Status = entity.AttemptCount >= maxAttempts ? "Failed" : "Pending";

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Same xmin concurrency concern as MarkCompletedAsync: if another replica already
            // applied a terminal state to this row, treat the conflict as a no-op.
        }
    }

    private static FeedbackCommentOutboxEntry ToEntry(FeedbackCommentOutboxEntity e) =>
        new()
        {
            Id = e.Id,
            RunId = e.RunId,
            IssueProviderConfigId = e.IssueProviderConfigId,
            IssueIdentifier = e.IssueIdentifier,
            RepoProviderConfigId = e.RepoProviderConfigId,
            PullRequestNumber = e.PullRequestNumber,
            FeedbackJson = e.FeedbackJson,
            Status = e.Status,
            AttemptCount = e.AttemptCount,
            CreatedAt = e.CreatedAt,
            LastAttemptAt = e.LastAttemptAt,
            CompletedAt = e.CompletedAt,
            ErrorMessage = e.ErrorMessage
        };

    /// <summary>
    /// Returns true when <paramref name="ex"/> represents a unique-constraint violation.
    /// Delegates to <see cref="PostgresErrorClassifier.IsUniqueViolation"/>.
    /// </summary>
    private static bool IsUniqueViolation(DbUpdateException ex)
        => PostgresErrorClassifier.IsUniqueViolation(ex);
}
