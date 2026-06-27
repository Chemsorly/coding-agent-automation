using System.Text.Json;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Singleton pipeline configuration. Maps to the "PipelineConfig" table.
/// </summary>
public class PipelineConfigEntity
{
    public Guid Id { get; set; }

    /// <summary>JSONB: full pipeline configuration.</summary>
    public JsonDocument? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
