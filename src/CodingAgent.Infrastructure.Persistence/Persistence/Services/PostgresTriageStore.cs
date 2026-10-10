using System.Text.Json;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed <see cref="ITriageStore"/>. The record lives in one JSONB column; the other columns
/// serve filtering. Whether a triage is running is never stored: it is an EXISTS on the active WorkItems
/// with the triage's key, the same key the partial unique index on WorkItems uses.
/// </summary>
public sealed class PostgresTriageStore : ITriageStore
{
    private const int MaxUpdateAttempts = 3;

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly TimeProvider _time;

    public PostgresTriageStore(IDbContextFactory<PipelineDbContext> dbFactory, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        _dbFactory = dbFactory;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<TriageRecord?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Triages.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, ct);
        return entity is null ? null : TriageEntityMapper.ToRecord(entity);
    }

    /// <inheritdoc/>
    public async Task<TriageRecord?> GetByIssueAsync(string issueProviderConfigId, string issueIdentifier, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.Triages.AsNoTracking()
            .FirstOrDefaultAsync(t => t.KeyProviderConfigId == issueProviderConfigId && t.KeyIdentifier == issueIdentifier, ct);
        return entity is null ? null : TriageEntityMapper.ToRecord(entity);
    }

    /// <inheritdoc/>
    public async Task<string?> GetActiveWorkItemIdAsync(TriageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        var (provider, identifier) = TriageEntityMapper.KeyOf(record);
        var active = PipelineConstants.ActiveWorkItemStatuses;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var id = await db.WorkItems.AsNoTracking()
            .Where(w => w.IssueIdentifier == identifier && w.IssueProviderConfigId == provider && active.Contains(w.Status))
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(ct);
        return id?.ToString();
    }

    /// <inheritdoc/>
    public async Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var active = PipelineConstants.ActiveWorkItemStatuses;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var triages = db.Triages.AsNoTracking();

        if (query.ProjectId is not null)
        {
            if (!Guid.TryParse(query.ProjectId, out var projectId))
                return new TriageListPage();
            triages = triages.Where(t => t.ProjectId == projectId);
        }

        if (query.Source is { } source)
        {
            var sourceName = source.ToString();
            triages = triages.Where(t => t.Source == sourceName);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            if (db.Database.IsNpgsql())
            {
                var pattern = "%" + EscapeLike(query.Search.Trim()) + "%";
                triages = triages.Where(t => EF.Functions.ILike(t.Title, pattern, "\\"));
            }
            else
            {
                // The InMemory test database has no ILIKE
                var search = query.Search.Trim().ToLowerInvariant();
                triages = triages.Where(t => t.Title.ToLower().Contains(search));
            }
        }

        // The list facts are small; the tab is decided by the resolved status, so it filters in memory.
        var rows = await triages
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new
            {
                t.Id, t.ProjectId, t.Source, t.KeyProviderConfigId, t.KeyIdentifier, t.State, t.Title, t.Facts,
                t.CreatedAt, t.UpdatedAt,
                IsActive = db.WorkItems.Any(w =>
                    w.IssueIdentifier == t.KeyIdentifier
                    && w.IssueProviderConfigId == t.KeyProviderConfigId
                    && active.Contains(w.Status)),
            })
            .ToListAsync(ct);

        var items = new List<TriageListItem>(rows.Count);
        foreach (var row in rows)
        {
            var facts = TriageEntityMapper.ReadFacts(row.Facts);
            var state = TriageEntityMapper.ParseState(row.State);
            var status = TriageStatusResolver.Resolve(
                state, row.IsActive, facts.AttemptCount > 0, facts.LastAttemptHasResult, facts.LastAttemptOutcome);
            if (!TriageStatusResolver.IsInTab(status, query.Tab))
                continue;

            var isIssue = TriageEntityMapper.ParseSource(row.Source) == TriageSource.Issue;
            items.Add(new TriageListItem
            {
                Id = row.Id,
                ProjectId = row.ProjectId.ToString("D"),
                Source = isIssue ? TriageSource.Issue : TriageSource.Operator,
                Title = row.Title,
                IssueProviderConfigId = isIssue ? row.KeyProviderConfigId : null,
                IssueIdentifier = isIssue ? row.KeyIdentifier : null,
                Status = status,
                Facts = facts,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
            });
        }

        var pageSize = Math.Clamp(query.PageSize, 1, 200);
        var page = Math.Max(query.Page, 1);
        return new TriageListPage
        {
            Items = items.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Total = items.Count,
        };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TriageRecord>> ListRecentAsync(
        string projectId, DateTimeOffset since, int max, Guid? excludeId, CancellationToken ct = default)
    {
        if (!Guid.TryParse(projectId, out var pid) || max <= 0)
            return [];

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.Triages.AsNoTracking()
            .Where(t => t.ProjectId == pid && t.UpdatedAt >= since && (excludeId == null || t.Id != excludeId))
            .OrderByDescending(t => t.UpdatedAt)
            .Take(max)
            .ToListAsync(ct);
        return entities.Select(TriageEntityMapper.ToRecord).ToList();
    }

    /// <inheritdoc/>
    public async Task CreateAsync(TriageRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.Triages.Add(TriageEntityMapper.ToEntity(record));
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<TriageRecord?> UpdateAsync(Guid id, Func<TriageRecord, TriageRecord?> mutate, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InTransactionAsync<TriageRecord?>(async (db, token) =>
                {
                    var entity = await LockAsync(db, id, token);
                    if (entity is null)
                        return null;

                    var current = TriageEntityMapper.ToRecord(entity);
                    var changed = mutate(current);
                    if (changed is null)
                        return current;

                    changed = changed with { UpdatedAt = _time.GetUtcNow() };
                    TriageEntityMapper.CopyInto(changed, entity);
                    await db.SaveChangesAsync(token);
                    return changed;
                }, ct);
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxUpdateAttempts)
            {
                // Backstop: the row lock serializes writers, so the version check should not fail.
            }
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> on a fresh context inside a transaction, as one unit of the context's execution
    /// strategy: the API's retrying Npgsql strategy refuses transactions it does not run itself, and a transient
    /// failure retries the whole unit, so <paramref name="body"/> must start from what it reads.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(Func<PipelineDbContext, CancellationToken, Task<T>> body, CancellationToken ct)
    {
        await using var strategyContext = await _dbFactory.CreateDbContextAsync(ct);
        var strategy = strategyContext.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            await using var db = await _dbFactory.CreateDbContextAsync(token);
            await using var tx = await db.Database.BeginTransactionAsync(token);
            var result = await body(db, token);
            await tx.CommitAsync(token);
            return result;
        }, ct);
    }

    /// <summary>
    /// Reads the row with <c>FOR UPDATE</c>, so concurrent writers of one triage wait for each other instead of
    /// failing their version check. Must run inside a transaction. The query is not composed further, so the
    /// locking clause stays at the top level. The InMemory test database has no row locks and reads the row plainly.
    /// </summary>
    private static async Task<TriageEntity?> LockAsync(PipelineDbContext db, Guid id, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return await db.Triages.FirstOrDefaultAsync(t => t.Id == id, ct);

        var rows = await db.Triages
            .FromSqlInterpolated($"SELECT *, xmin FROM \"Triages\" WHERE \"Id\" = {id} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    private static async Task<TriageEntity?> LockByKeyAsync(PipelineDbContext db, string provider, string identifier, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql())
            return await db.Triages.FirstOrDefaultAsync(t => t.KeyProviderConfigId == provider && t.KeyIdentifier == identifier, ct);

        var rows = await db.Triages
            .FromSqlInterpolated($"SELECT *, xmin FROM \"Triages\" WHERE \"KeyProviderConfigId\" = {provider} AND \"KeyIdentifier\" = {identifier} FOR UPDATE")
            .ToListAsync(ct);
        return rows.Count > 0 ? rows[0] : null;
    }

    /// <inheritdoc/>
    public async Task<TriageRecord> RecordResultAsync(TriageResultReport report, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(report);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await InTransactionAsync(async (db, token) =>
                {
                    var entity = await LockByKeyAsync(db, report.KeyProviderConfigId, report.KeyIdentifier, token);

                    var now = _time.GetUtcNow();
                    TriageRecord current;
                    if (entity is not null)
                    {
                        current = TriageEntityMapper.ToRecord(entity);
                    }
                    else if (report.Source == TriageSource.Issue)
                    {
                        // A tracker issue's first result creates its triage
                        current = new TriageRecord
                        {
                            Id = Guid.NewGuid(),
                            ProjectId = report.ProjectId,
                            Source = TriageSource.Issue,
                            IssueProviderConfigId = report.KeyProviderConfigId,
                            IssueIdentifier = report.KeyIdentifier,
                            IssueUrl = report.IssueUrl,
                            Title = string.IsNullOrWhiteSpace(report.IssueTitle) ? $"#{report.KeyIdentifier}" : report.IssueTitle,
                            CreatedAt = now,
                            UpdatedAt = now,
                        };
                    }
                    else
                    {
                        throw new InvalidOperationException(
                            $"No triage exists for {report.KeyIdentifier}; an operator triage is created before its first run");
                    }

                    var updated = ApplyResult(current, report, now);
                    if (entity is null)
                        db.Triages.Add(TriageEntityMapper.ToEntity(updated));
                    else
                        TriageEntityMapper.CopyInto(updated, entity);

                    await db.SaveChangesAsync(token);
                    return updated;
                }, ct);
            }
            catch (DbUpdateException) when (attempt < MaxUpdateAttempts)
            {
                // A concurrent writer changed the row, or created the tracker issue's triage first: retry on
                // the stored version.
            }
        }
    }

    /// <inheritdoc/>
    public async Task<int> BackfillEndedAttemptsAsync(CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var openIds = await db.Triages.AsNoTracking().Where(t => t.HasOpenAttempt).Select(t => t.Id).ToListAsync(ct);

        var changed = 0;
        foreach (var id in openIds)
        {
            var openWorkItemIds = (await GetAsync(id, ct))?.Attempts
                .Where(a => a.Result is null && a.Outcome is null)
                .Select(a => Guid.TryParse(a.WorkItemId, out var w) ? w : Guid.Empty)
                .ToList() ?? [];
            if (openWorkItemIds.Count == 0)
                continue;

            var workItems = await db.WorkItems.AsNoTracking()
                .Where(w => openWorkItemIds.Contains(w.Id))
                .Select(w => new { w.Id, w.Status, w.CompletedAt, w.ErrorMessage })
                .ToDictionaryAsync(w => w.Id.ToString(), ct);

            var didChange = false;
            await UpdateAsync(id, record =>
            {
                var any = false;
                var attempts = record.Attempts.Select(a =>
                {
                    if (a.Result is not null || a.Outcome is not null)
                        return a;

                    if (!workItems.TryGetValue(a.WorkItemId, out var workItem))
                    {
                        any = true;
                        return a with { Outcome = TriageAttemptOutcome.Failed, FailureReason = a.FailureReason ?? "The run ended without reporting a result." };
                    }

                    var outcome = workItem.Status switch
                    {
                        WorkItemStatus.Cancelled => TriageAttemptOutcome.Cancelled,
                        WorkItemStatus.Failed or WorkItemStatus.Succeeded => TriageAttemptOutcome.Failed,
                        _ => (TriageAttemptOutcome?)null
                    };
                    if (outcome is null)
                        return a;

                    any = true;
                    return a with
                    {
                        Outcome = outcome,
                        CompletedAt = workItem.CompletedAt ?? a.CompletedAt,
                        FailureReason = outcome == TriageAttemptOutcome.Failed
                            ? workItem.ErrorMessage ?? "The run ended without reporting a result."
                            : null,
                    };
                }).ToList();

                didChange = any;
                return any ? record with { Attempts = attempts } : null;
            }, ct);

            if (didChange)
                changed++;
        }

        return changed;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        var active = PipelineConstants.ActiveWorkItemStatuses;
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        return await db.Triages
            .Where(t => t.UpdatedAt < cutoff
                && !db.WorkItems.Any(w =>
                    w.IssueIdentifier == t.KeyIdentifier
                    && w.IssueProviderConfigId == t.KeyProviderConfigId
                    && active.Contains(w.Status)))
            .ExecuteDeleteAsync(ct);
    }

    /// <summary>
    /// Completes (or adds) the report's attempt, replaces the drafts with the new result's and moves the
    /// state to the verdict's.
    /// </summary>
    internal static TriageRecord ApplyResult(TriageRecord record, TriageResultReport report, DateTimeOffset now)
    {
        var attempts = record.Attempts.ToList();
        var index = attempts.FindIndex(a => a.WorkItemId == report.WorkItemId);
        if (index >= 0)
        {
            attempts[index] = attempts[index] with
            {
                Result = report.Result,
                CompletedAt = now,
                Outcome = TriageAttemptOutcome.Completed,
                FailureReason = null,
            };
        }
        else
        {
            attempts.Add(new TriageAttempt
            {
                WorkItemId = report.WorkItemId,
                StartedAt = report.StartedAt,
                CompletedAt = now,
                Result = report.Result,
                Outcome = TriageAttemptOutcome.Completed,
                // A tracker issue's re-run is asked for on the tracker: its feedback is in the issue comments
            });
        }

        return record with
        {
            Attempts = attempts,
            Drafts = report.Result.Drafts
                .Select(d => new TriageEditableDraft { Current = d, Original = d })
                .ToList(),
            DraftsFromWorkItemId = report.WorkItemId,
            State = TriageStatusResolver.StateFor(report.Result.Verdict),
            IssueUrl = record.IssueUrl ?? report.IssueUrl,
            DismissReason = null,
            DismissedBy = null,
            UpdatedAt = now,
        };
    }

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);
}

/// <summary>Maps <see cref="TriageRecord"/> to and from <see cref="TriageEntity"/>.</summary>
public static class TriageEntityMapper
{
    /// <summary>The triage's WorkItem key: the issue for a tracker triage, the sentinel key for an operator triage.</summary>
    public static (string ProviderConfigId, string Identifier) KeyOf(TriageRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return record.Source == TriageSource.Issue
            ? (record.IssueProviderConfigId ?? "", record.IssueIdentifier ?? "")
            : (TriageConstants.ProviderConfigId, TriageConstants.IssueIdentifierFor(record.Id));
    }

    public static TriageEntity ToEntity(TriageRecord record)
    {
        var entity = new TriageEntity { Id = record.Id, CreatedAt = record.CreatedAt };
        CopyInto(record, entity);
        return entity;
    }

    /// <summary>Writes the record and every column derived from it into the entity.</summary>
    public static void CopyInto(TriageRecord record, TriageEntity entity)
    {
        ArgumentNullException.ThrowIfNull(record);
        ArgumentNullException.ThrowIfNull(entity);

        var (provider, identifier) = KeyOf(record);
        entity.ProjectId = Guid.TryParse(record.ProjectId, out var pid)
            ? pid
            : throw new ArgumentException($"Project id '{record.ProjectId}' is not a GUID", nameof(record));
        entity.Source = record.Source.ToString();
        entity.KeyProviderConfigId = provider;
        entity.KeyIdentifier = identifier;
        entity.State = record.State.ToString();
        entity.Title = record.Title;
        entity.HasOpenAttempt = record.Attempts.Any(a => a.Result is null && a.Outcome is null);
        entity.Facts = JsonSerializer.Serialize(TriageListFacts.From(record), PipelineJsonOptions.Default);
        entity.Data = JsonSerializer.Serialize(record, PipelineJsonOptions.Default);
        entity.UpdatedAt = record.UpdatedAt;
    }

    public static TriageRecord ToRecord(TriageEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return JsonSerializer.Deserialize<TriageRecord>(entity.Data, PipelineJsonOptions.Lenient)
            ?? throw new InvalidOperationException($"Triage {entity.Id} has no data");
    }

    public static TriageListFacts ReadFacts(string json) =>
        JsonSerializer.Deserialize<TriageListFacts>(json, PipelineJsonOptions.Lenient) ?? new TriageListFacts();

    public static TriageState ParseState(string value) =>
        Enum.TryParse<TriageState>(value, ignoreCase: true, out var state) ? state : TriageState.New;

    public static TriageSource ParseSource(string value) =>
        Enum.TryParse<TriageSource>(value, ignoreCase: true, out var source) ? source : TriageSource.Operator;
}
