using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Stores triages. Only the API reads or writes them; every other service goes through the API.
/// </summary>
public interface ITriageStore
{
    Task<TriageRecord?> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>Finds the triage of a tracker issue.</summary>
    Task<TriageRecord?> GetByIssueAsync(string issueProviderConfigId, string issueIdentifier, CancellationToken ct = default);

    /// <summary>The id of the triage's running WorkItem (Pending, Dispatched or Running), or null.</summary>
    Task<string?> GetActiveWorkItemIdAsync(TriageRecord record, CancellationToken ct = default);

    /// <summary>A page of the triage list, newest change first. <see cref="TriageListItem.LatestTokens"/> is not set.</summary>
    Task<TriageListPage> ListAsync(TriageListQuery query, CancellationToken ct = default);

    /// <summary>The project's triages changed since <paramref name="since"/>, newest first, without <paramref name="excludeId"/>.</summary>
    Task<IReadOnlyList<TriageRecord>> ListRecentAsync(
        string projectId, DateTimeOffset since, int max, Guid? excludeId, CancellationToken ct = default);

    /// <summary>Inserts a new triage.</summary>
    Task CreateAsync(TriageRecord record, CancellationToken ct = default);

    /// <summary>
    /// Applies <paramref name="mutate"/> to the stored record and saves it, retrying on a concurrent
    /// change. <paramref name="mutate"/> returns null to leave the record unchanged. Returns the saved
    /// record, or null when the triage does not exist.
    /// </summary>
    Task<TriageRecord?> UpdateAsync(Guid id, Func<TriageRecord, TriageRecord?> mutate, CancellationToken ct = default);

    /// <summary>
    /// Records an attempt's result: completes the attempt with the report's WorkItem id (adding it when the
    /// triage has none yet), replaces the drafts and moves the state. A tracker issue's first result creates
    /// its triage. Returns the saved record.
    /// </summary>
    Task<TriageRecord> RecordResultAsync(TriageResultReport report, CancellationToken ct = default);

    /// <summary>
    /// Copies the outcome of attempts that ended without a result from their WorkItems, before the
    /// WorkItems expire. An attempt whose WorkItem no longer exists is marked failed. Returns the number of
    /// triages changed.
    /// </summary>
    Task<int> BackfillEndedAttemptsAsync(CancellationToken ct = default);

    /// <summary>Deletes triages last changed before <paramref name="cutoff"/> that have no running WorkItem.</summary>
    Task<int> DeleteExpiredAsync(DateTimeOffset cutoff, CancellationToken ct = default);
}
