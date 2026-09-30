using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// API-backed implementation of <see cref="IHarnessSuggestionStore"/>.
/// Delegates all persistence calls to the Pipeline API instead of the database.
/// </summary>
public sealed class ApiBackedHarnessSuggestionStore : IHarnessSuggestionStore
{
    private readonly IPipelineApiHarnessSuggestionClient _client;

    public ApiBackedHarnessSuggestionStore(IPipelineApiHarnessSuggestionClient client)
    {
        _client = client;
    }

    public Task<HarnessSuggestions?> LoadAsync(CancellationToken ct)
        => _client.GetAsync(ct);

    public Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
        => _client.SaveAsync(suggestions, ct);
}
