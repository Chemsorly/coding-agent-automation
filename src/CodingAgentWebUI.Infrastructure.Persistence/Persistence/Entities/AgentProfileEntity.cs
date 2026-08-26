namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Agent profile configuration. Maps to the "AgentProfiles" table.
/// </summary>
public class AgentProfileEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSONB string: full agent profile configuration.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
