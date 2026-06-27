using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Provider configuration (issue, repository, agent, pipeline, brain). Maps to the "ProviderConfigs" table.
/// </summary>
public class ProviderConfigEntity
{
    public Guid Id { get; set; }
    public ProviderKind Kind { get; set; }
    public string DisplayName { get; set; } = "";
    public string ProviderType { get; set; } = "";
    public bool Enabled { get; set; }

    /// <summary>JSONB string: provider-specific configuration fields.</summary>
    public string? Configuration { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
