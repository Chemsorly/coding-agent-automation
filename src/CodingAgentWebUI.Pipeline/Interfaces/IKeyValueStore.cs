namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Key/value persistence abstraction backed by the <c>KeyValueStore</c> table.
/// Lives in Pipeline (not Infrastructure) so <c>CodingAgentWebUI.Api.Client</c> can reference it
/// in Spec 042 without depending on Infrastructure.
/// </summary>
public interface IKeyValueStore
{
    /// <summary>Returns the value for <paramref name="key"/>, or <c>null</c> if the key does not exist.</summary>
    Task<string?> GetAsync(string key, CancellationToken ct);

    /// <summary>Inserts or updates the value for <paramref name="key"/>.</summary>
    Task SetAsync(string key, string value, CancellationToken ct);

    /// <summary>Deletes the entry for <paramref name="key"/>. No-op if the key does not exist.</summary>
    Task DeleteAsync(string key, CancellationToken ct);
}
