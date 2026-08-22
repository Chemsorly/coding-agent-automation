using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed implementation of <see cref="IKeyValueStore"/>.
/// Stores values as plain strings in the <c>KeyValueStore</c> table.
/// Uses a context-per-operation pattern via <see cref="IDbContextFactory{TContext}"/>.
/// Registered as <c>AddScoped&lt;IKeyValueStore, EfKeyValueStore&gt;()</c>.
/// </summary>
public sealed class EfKeyValueStore : IKeyValueStore
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public EfKeyValueStore(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <inheritdoc/>
    public async Task<string?> GetAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyValueStore.AsNoTracking()
            .FirstOrDefaultAsync(kv => kv.Key == key, ct);

        return entity?.Value;
    }

    /// <inheritdoc/>
    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var entity = await db.KeyValueStore.FindAsync([key], ct);
        if (entity is not null)
        {
            entity.Value = value;
        }
        else
        {
            db.KeyValueStore.Add(new KeyValueEntity { Key = key, Value = value });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.KeyValueStore.FindAsync([key], ct);
        if (entity is not null)
        {
            db.KeyValueStore.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }
}
