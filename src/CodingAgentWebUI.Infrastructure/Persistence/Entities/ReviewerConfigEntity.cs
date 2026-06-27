using System.Text.Json;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Reviewer configuration. Maps to the "ReviewerConfigs" table.
/// </summary>
public class ReviewerConfigEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSONB: full reviewer configuration.</summary>
    public JsonDocument? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
