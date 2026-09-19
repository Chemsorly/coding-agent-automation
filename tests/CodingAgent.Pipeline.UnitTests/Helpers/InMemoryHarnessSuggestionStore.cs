using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Helpers;

/// <summary>
/// In-memory test double for <see cref="IHarnessSuggestionStore"/>.
/// Replaces the deleted <c>FileSystemHarnessSuggestionStore</c> in consolidation service tests
/// that need a functional (non-mocked) store without filesystem I/O.
/// </summary>
// TODO [WARNING]: _stored is not thread-safe. ConsolidationServiceOnChangeTests wires OnChange event callbacks
// that could race with a SaveAsync call if the consolidation service fires the event from a background thread.
// The deleted FileSystemHarnessSuggestionStore had the same limitation (no lock on file writes), so this is
// not a regression, but add locking (e.g. lock or Interlocked) if concurrent test paths are introduced.
public sealed class InMemoryHarnessSuggestionStore : IHarnessSuggestionStore
{
    private HarnessSuggestions? _stored;

    public Task<HarnessSuggestions?> GetAsync(CancellationToken ct)
        => Task.FromResult(_stored);

    public Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(suggestions);
        // TODO [WARNING]: _stored is assigned the same object reference, not a deep copy. The deleted
        // FileSystemHarnessSuggestionStore enforced value-copy semantics via JSON serialization. Tests that
        // mutate the HarnessSuggestions object after SaveAsync and then call GetAsync will see the mutated
        // object, which could allow a defective implementation that shares references to pass undetected.
        // Consider serializing to/from JSON here to restore the isolation semantics.
        _stored = suggestions;
        return Task.CompletedTask;
    }
}
