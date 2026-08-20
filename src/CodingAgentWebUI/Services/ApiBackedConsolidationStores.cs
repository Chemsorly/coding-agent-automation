using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// API-backed implementation of <see cref="IConsolidationRunStore"/>.
/// Delegates all persistence calls to the Pipeline API instead of the database,
/// ensuring the API remains the single authoritative writer for consolidation runs.
/// </summary>
internal sealed class ApiBackedConsolidationRunStore : IConsolidationRunStore
{
    private readonly IPipelineApiConsolidationRunClient _client;

    public ApiBackedConsolidationRunStore(IPipelineApiConsolidationRunClient client)
    {
        _client = client;
    }

    public Task SaveRunAsync(ConsolidationRun run, CancellationToken ct)
        => _client.SaveRunAsync(run, ct);

    public Task<IReadOnlyList<ConsolidationRun>> LoadAllRunsAsync(CancellationToken ct)
        => _client.LoadAllRunsAsync(ct);

    public Task<ConsolidationRun?> GetByIdAsync(RunId runId, CancellationToken ct)
        => _client.GetByIdAsync(runId.Value, ct);

    public Task DeleteRunAsync(RunId runId, CancellationToken ct)
        => _client.DeleteRunAsync(runId.Value, ct);
}

/// <summary>
/// API-backed implementation of <see cref="IHarnessSuggestionStore"/>.
/// Delegates all persistence calls to the Pipeline API instead of the database.
/// </summary>
internal sealed class ApiBackedHarnessSuggestionStore : IHarnessSuggestionStore
{
    private readonly IPipelineApiHarnessSuggestionClient _client;

    public ApiBackedHarnessSuggestionStore(IPipelineApiHarnessSuggestionClient client)
    {
        _client = client;
    }

    public Task<HarnessSuggestions?> GetAsync(CancellationToken ct)
        => _client.GetAsync(ct);

    public Task SaveAsync(HarnessSuggestions suggestions, CancellationToken ct)
        => _client.SaveAsync(suggestions, ct);
}
