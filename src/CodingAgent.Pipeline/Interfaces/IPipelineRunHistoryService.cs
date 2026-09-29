using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Manages pipeline run history: persistence, retrieval, and workspace cleanup.
/// </summary>
public interface IPipelineRunHistoryService
{
    void TryDeleteWorkspace(WorkspacePath? workspacePath, string runId, string workspaceBaseDirectory);
    void CleanupExpiredWorkspaces(PipelineConfiguration config, string? activeRunId = null);

    /// <summary>Persists a completed run to history.</summary>
    Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default);

    /// <summary>
    /// Persists a pre-serialized run summary directly, bypassing the <see cref="PipelineRun"/> wrapper.
    /// Used by the API endpoint when the orchestrator sends a summary over HTTP rather than writing DB directly.
    /// </summary>
    Task AddRunSummaryAsync(PipelineRunSummary summary, CancellationToken ct = default);

    /// <summary>Retrieves the run history.</summary>
    Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default);

    /// <summary>Retrieves the run history with pagination.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>Retrieves paginated run history filtered to runs that have feedback.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Number of items per page.</param>
    /// <param name="feedbackOnly">When true, returns only runs with non-null Feedback. Filter is applied in the DB query, not after paging.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, CancellationToken ct = default);

    /// <summary>
    /// Retrieves paginated run history filtered by outcome (<paramref name="finalStep"/>) in addition to
    /// the optional feedback filter — the DB applies the outcome filter before paging, so pagination stays
    /// correct across the whole history.
    /// </summary>
    /// <param name="finalStep">When set, returns only runs whose terminal step matches (e.g. <see cref="PipelineStep.Failed"/>); null = no outcome filter.</param>
    /// <param name="projectId">When set, returns only runs in that project; null = all projects.</param>
    /// <remarks>
    /// The default implementation ignores <paramref name="finalStep"/> and <paramref name="projectId"/>.
    /// Only the DB-backed <c>PostgresPipelineRunHistoryService</c> (the API endpoint's implementor) overrides
    /// it; the other implementors — the agent's null service and the API-client-backed stores — are not on
    /// the filter path (the web UI's pages call <c>IPipelineApiRunHistoryClient</c> directly).
    /// </remarks>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, CancellationToken ct = default)
        => GetRunHistoryAsync(page, pageSize, feedbackOnly, ct);

    /// <summary>
    /// Retrieves paginated run history with a server-side date filter in addition to the outcome and project filters.
    /// The <paramref name="since"/> filter is applied in the DB query before paging, so <see cref="PagedResult{T}.HasMore"/>
    /// accurately reflects whether in-window runs overflow the requested page size.
    /// </summary>
    /// <param name="since">When set, returns only runs whose <c>StartedAt</c> is &gt;= this value. Pass <c>null</c> for no date filter (equivalent to "All" window).</param>
    /// <remarks>
    /// <para>
    /// The default implementation ignores <paramref name="since"/> and delegates to
    /// <see cref="GetRunHistoryAsync(int,int,bool,PipelineStep?,string?,CancellationToken)"/>.
    /// Only <c>PostgresPipelineRunHistoryService</c> overrides this overload to push the filter to the DB.
    /// </para>
    /// <para>
    /// <b>Services that do NOT override this overload (silent no-op for <paramref name="since"/>):</b>
    /// <list type="bullet">
    ///   <item><c>PipelineRunHistoryService</c> (file-backed) — used only in non-Postgres local deployments; not on the Insights call path.</item>
    ///   <item><c>ApiBackedPipelineRunHistoryService</c> — the orchestrator's HTTP-bridged service; <c>Insights.razor</c> calls
    ///   <c>IPipelineApiRunHistoryClient</c> directly and never routes through this service, so the no-op is harmless.</item>
    /// </list>
    /// </para>
    /// </remarks>
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, CancellationToken ct = default)
        => GetRunHistoryAsync(page, pageSize, feedbackOnly, finalStep, projectId, ct);

    /// <summary>
    /// Retrieves paginated run history filtered by <see cref="PipelineRunType"/> in addition to all other filters.
    /// The <paramref name="runType"/> filter is applied in the DB query before paging.
    /// </summary>
    /// <param name="runType">When set, returns only runs of this <see cref="PipelineRunType"/>; null = all run types.</param>
    /// <remarks>
    /// The default implementation ignores <paramref name="runType"/> and delegates to the <paramref name="since"/> overload.
    /// Only <c>PostgresPipelineRunHistoryService</c> overrides this overload to push the filter to the DB.
    /// </remarks>
    // TODO [WARNING]: The default implementation silently ignores the runType parameter. Any
    // IPipelineRunHistoryService implementation that does not override this overload (including
    // NullPipelineRunHistoryService in DatabaseMaintenanceService and in-memory test doubles) will
    // return all run types regardless of the filter, producing incorrect results for callers that
    // pass runType: PipelineRunType.Consolidation. Only PostgresPipelineRunHistoryService correctly
    // applies the filter. Document this contract clearly or consider adding a compile-time guard
    // (e.g. abstract method) to prevent silent filter-ignore in future implementations.
    // (DotNetSpecialist review)
    Task<PagedResult<PipelineRunSummary>> GetRunHistoryAsync(int page, int pageSize, bool feedbackOnly, PipelineStep? finalStep, string? projectId, DateTimeOffset? since, PipelineRunType? runType, CancellationToken ct = default)
        => GetRunHistoryAsync(page, pageSize, feedbackOnly, finalStep, projectId, since, ct);

    /// <summary>Retrieves a single pipeline run summary by run ID. Returns null if not found.</summary>
    Task<PipelineRunSummary?> GetRunAsync(Guid runId, CancellationToken ct = default);
}
