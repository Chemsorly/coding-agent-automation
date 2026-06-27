using System.Text.Json;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Project configuration with typed columns + JSONB settings. Maps to the "Projects" table.
/// </summary>
public class ProjectEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public bool Enabled { get; set; }
    public string? Description { get; set; }

    /// <summary>JSONB: prompts, timeouts, secrets ref, and other project-specific settings.</summary>
    public JsonDocument? Settings { get; set; }

    public List<string> TemplateIds { get; set; } = [];

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
