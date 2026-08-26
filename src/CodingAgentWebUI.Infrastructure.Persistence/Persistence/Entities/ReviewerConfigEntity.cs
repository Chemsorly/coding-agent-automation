namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Reviewer configuration. Maps to the "ReviewerConfigs" table.
/// </summary>
public class ReviewerConfigEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSONB string: full reviewer configuration.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
