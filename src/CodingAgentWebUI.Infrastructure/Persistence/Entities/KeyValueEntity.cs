namespace CodingAgentWebUI.Infrastructure.Persistence.Entities;

/// <summary>
/// Generic key-value store for small data that doesn't warrant its own table.
/// Used for: loop state, harness suggestions, etc.
/// Maps to the "KeyValueStore" table.
/// </summary>
public class KeyValueEntity
{
    /// <summary>Well-known string key (e.g., "loop-state", "harness-suggestions").</summary>
    public required string Key { get; set; }

    /// <summary>JSONB payload.</summary>
    public string? Value { get; set; }

    /// <summary>Concurrency token mapped to PostgreSQL xmin system column.</summary>
    public uint RowVersion { get; set; }
}
