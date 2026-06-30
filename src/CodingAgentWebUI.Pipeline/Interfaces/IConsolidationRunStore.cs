using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Persistence abstraction for consolidation runs.
/// Implementations: filesystem (legacy) or database (DB mode).
/// </summary>
public interface IConsolidationRunStore
{
    /// <summary>Persists a consolidation run (insert or update).</summary>
    Task SaveRunAsync(ConsolidationRun run, CancellationToken ct);

    /// <summary>Loads all persisted consolidation runs.</summary>
    Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct);

    /// <summary>Deletes a persisted run by ID.</summary>
    Task DeleteRunAsync(string runId, CancellationToken ct);
}
