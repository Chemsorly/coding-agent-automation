namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Consolidation run data. Maps to the "ConsolidationRuns" table.
/// </summary>
public class ConsolidationRunEntity
{
    public Guid Id { get; set; }

    /// <summary>JSONB string: full consolidation run data.</summary>
    public string? Data { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
