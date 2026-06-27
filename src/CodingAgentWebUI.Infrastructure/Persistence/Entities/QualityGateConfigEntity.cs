namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Quality gate configuration. Maps to the "QualityGateConfigs" table.
/// </summary>
public class QualityGateConfigEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSONB string: full quality gate configuration.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
