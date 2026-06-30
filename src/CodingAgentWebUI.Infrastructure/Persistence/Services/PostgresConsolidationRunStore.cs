using System.Text.Json;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Infrastructure.Persistence.Services;

/// <summary>
/// Database-backed consolidation run store. Reads/writes to the ConsolidationRuns table.
/// </summary>
public sealed class PostgresConsolidationRunStore : IConsolidationRunStore
{
    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;

    public PostgresConsolidationRunStore(IDbContextFactory<PipelineDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveRunAsync(ConsolidationRun run, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var id = Guid.TryParse(run.RunId, out var guid) ? guid : Guid.NewGuid();
        var json = JsonSerializer.Serialize(run, PipelineJsonOptions.Default);

        var existing = await db.ConsolidationRuns.FindAsync([id], ct);
        if (existing is not null)
        {
            existing.Data = json;
        }
        else
        {
            db.ConsolidationRuns.Add(new ConsolidationRunEntity { Id = id, Data = json });
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entities = await db.ConsolidationRuns.AsNoTracking().ToListAsync(ct);

        var runs = new List<ConsolidationRun>();
        foreach (var entity in entities)
        {
            if (string.IsNullOrEmpty(entity.Data)) continue;
            var run = JsonSerializer.Deserialize<ConsolidationRun>(entity.Data, PipelineJsonOptions.Default);
            if (run is not null)
                runs.Add(run);
        }

        return runs;
    }

    public async Task DeleteRunAsync(string runId, CancellationToken ct)
    {
        if (!Guid.TryParse(runId, out var id)) return;

        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        var entity = await db.ConsolidationRuns.FindAsync([id], ct);
        if (entity is not null)
        {
            db.ConsolidationRuns.Remove(entity);
            await db.SaveChangesAsync(ct);
        }
    }
}
