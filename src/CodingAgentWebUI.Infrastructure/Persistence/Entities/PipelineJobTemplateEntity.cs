namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Pipeline job template associated with a project. Maps to the "PipelineJobTemplates" table.
/// </summary>
public class PipelineJobTemplateEntity
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Name { get; set; } = "";

    /// <summary>JSONB string: full job template configuration.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
