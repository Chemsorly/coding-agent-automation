using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Persistence abstraction for harness improvement suggestions.
/// Implementations: filesystem (legacy) or database (DB mode).
/// </summary>
public interface IHarnessSuggestionStore
{
    /// <summary>Loads persisted harness suggestions. Returns null if none exist.</summary>
    Task<HarnessSuggestions?> GetAsync(CancellationToken ct);

    /// <summary>Persists harness suggestions (overwrites any existing).</summary>
    Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct);
}
