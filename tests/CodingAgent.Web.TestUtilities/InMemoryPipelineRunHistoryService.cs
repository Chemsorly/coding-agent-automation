using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// In-memory pipeline run history service. No file I/O.
/// </summary>
public sealed class InMemoryPipelineRunHistoryService : IPipelineRunHistoryService
{
    private readonly List<PipelineRunSummary> _history = new();

    public void Reset() => _history.Clear();

    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<PipelineRunSummary>>(_history.ToList().AsReadOnly());

    // TODO: This fake does not filter by InitiatedBy != ConsolidationConstants.InitiatedBy, unlike the real
    // PostgresPipelineRunHistoryService. If E2E tests rely on this for pagination validation, results may
    // diverge from production behavior. Consider adding a contract test or aligning the filter logic.
    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default)
    {
        // Mirror PostgresPipelineRunHistoryService: only terminal runs appear in history listings.
        // AddRunToHistoryAsync in the real service forces non-terminal steps to Failed before storing,
        // so active (non-terminal) runs are never returned by GetRunHistoryAsync in production.
        var terminal = _history.Where(r => r.FinalStep.IsTerminal()).ToList();
        var items = terminal.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        });
    }

    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
    {
        _history.Insert(0, run.ToSummary());
        return Task.CompletedTask;
    }

    public Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default)
    {
        var match = _history.FirstOrDefault(r => r.RunId == runId.ToString());
        return Task.FromResult<PipelineRunSummary?>(match);
    }

    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default)
    {
        // Mirror PostgresPipelineRunHistoryService: only terminal runs appear in history listings.
        var filtered = _history.Where(r => r.FinalStep.IsTerminal());
        if (feedbackOnly)
            filtered = filtered.Where(r => r.Feedback != null);
        var list = filtered.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        });
    }

    /// <summary>
    /// Full overload that respects the <paramref name="finalStep"/> and
    /// <paramref name="projectId"/> filters, matching the DB-side exact-match semantics of
    /// <c>PostgresPipelineRunHistoryService</c>. Without this override the default
    /// interface implementation ignores both parameters and returns all runs, which makes
    /// the Runs-page tab-membership assertions vacuous in the E2E harness.
    /// </summary>
    // TODO [WARNING]: This overload filters by r.FinalStep == step (exact match). If
    // PostgresPipelineRunHistoryService maps a tab (e.g. "Completed") to multiple PipelineSteps
    // (e.g. both Completed and PrMerged), the fake and the real service will disagree, making
    // tab-membership assertions pass here while the real page misbehaves. Verify the production
    // filter semantics and align this implementation accordingly.
    // TODO [WARNING]: _history is iterated without synchronisation. If any other test in the
    // same E2ECollection concurrently calls AddRunSummaryAsync while this method is running its
    // LINQ chain, the enumeration will throw InvalidOperationException. If parallelism is ever
    // enabled for this collection, take a snapshot (e.g. lock (_history) { list = _history.ToList(); })
    // before filtering.
    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(
        int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep,
        string? projectId, CancellationToken ct = default)
    {
        // Mirror PostgresPipelineRunHistoryService: only terminal runs appear in history listings.
        IEnumerable<PipelineRunSummary> filtered = _history.Where(r => r.FinalStep.IsTerminal());
        if (feedbackOnly)
            filtered = filtered.Where(r => r.Feedback != null);
        if (finalStep is { } step)
            filtered = filtered.Where(r => r.FinalStep == step);
        if (!string.IsNullOrEmpty(projectId))
            filtered = filtered.Where(r => r.ProjectId == projectId);

        var list = filtered.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        });
    }

    /// <summary>
    /// Full overload that also respects the <paramref name="since"/> date filter, matching the
    /// DB-side <c>StartedAt >= since</c> semantics of <c>PostgresPipelineRunHistoryService</c>.
    /// Without this override the default interface implementation ignores <paramref name="since"/>
    /// and returns all runs regardless of the time window, causing Insights E2E assertions to fail.
    /// </summary>
    public Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(
        int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep,
        string? projectId, DateTimeOffset? since, CancellationToken ct = default)
    {
        IEnumerable<PipelineRunSummary> filtered = _history.Where(r => r.FinalStep.IsTerminal());
        if (feedbackOnly)
            filtered = filtered.Where(r => r.Feedback != null);
        if (finalStep is { } step)
            filtered = filtered.Where(r => r.FinalStep == step);
        if (!string.IsNullOrEmpty(projectId))
            filtered = filtered.Where(r => r.ProjectId == projectId);
        if (since is { } sinceValue)
            filtered = filtered.Where(r => r.StartedAtOffset >= sinceValue);

        var list = filtered.ToList();
        var items = list.Skip((page - 1) * pageSize).Take(pageSize + 1).ToList();
        var hasMore = items.Count > pageSize;
        if (hasMore)
            items = items.Take(pageSize).ToList();
        return Task.FromResult(new PagedResult<PipelineRunSummary>
        {
            Items = items.AsReadOnly(),
            Page = page,
            PageSize = pageSize,
            HasMore = hasMore
        });
    }

    public Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default)
    {
        _history.Insert(0, summary);
        return Task.CompletedTask;
    }
}
