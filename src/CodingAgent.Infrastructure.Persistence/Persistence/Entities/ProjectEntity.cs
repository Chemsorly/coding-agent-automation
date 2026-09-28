namespace CodingAgent.Infrastructure.Persistence.Entities;

/// <summary>
/// Project configuration with typed columns + JSONB settings. Maps to the "Projects" table.
/// A project's templates are the rows whose <see cref="PipelineJobTemplateEntity.ProjectId"/> points here.
/// </summary>
public class ProjectEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }

    /// <summary>JSONB string: prompts, timeouts, secrets ref, and other project-specific settings.</summary>
    public string? Settings { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
