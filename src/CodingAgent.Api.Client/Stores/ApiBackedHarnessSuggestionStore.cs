using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client.Stores;

/// <summary>
/// API-backed implementation of <see cref="IHarnessSuggestionStore"/>.
/// Delegates all persistence calls to the Pipeline API instead of the database.
/// </summary>
public sealed class ApiBackedHarnessSuggestionStore : IHarnessSuggestionStore
{
    private readonly IPipelineApiHarnessSuggestionClient _client;

    public ApiBackedHarnessSuggestionStore(IPipelineApiHarnessSuggestionClient client)
    {
        // TODO [WARNING]: Add ArgumentNullException.ThrowIfNull(client) here. A null argument
        // silently passes construction and causes a NullReferenceException on the first call to
        // LoadAsync or SaveAsync. ConsolidationSelectorResolver (moved in the same change) guards
        // both its constructor parameters with ThrowIfNull — this class should do the same.
        _client = client;
    }

    public Task<HarnessSuggestions?> LoadAsync(CancellationToken ct)
        => _client.GetAsync(ct);

    public Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
        => _client.SaveAsync(suggestions, ct);
}
