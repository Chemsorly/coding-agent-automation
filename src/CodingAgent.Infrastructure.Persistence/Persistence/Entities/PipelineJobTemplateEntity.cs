namespace CodingAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// Pipeline job template associated with a project. Maps to the "PipelineJobTemplates" table.
/// </summary>
public class PipelineJobTemplateEntity
{
    public Guid Id { get; set; }

    /// <summary>The project the template belongs to: the only record of project membership.</summary>
    public Guid ProjectId { get; set; }

    /// <summary>Copy of the template's name, used for the order of a project's templates.</summary>
    public string Name { get; set; } = "";

    /// <summary>JSONB string: full job template configuration.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
