using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// Dispatches a consolidation run to the K8s job queue by calling
/// <see cref="IWorkDistributor.DistributeAsync"/> with the correct
/// <see cref="JobDistributionRequest"/> for the given run.
/// <para>
/// Separates the dispatch concern from <see cref="IConsolidationService"/> (state management)
/// so that both the UI trigger path and startup rehydration share one implementation without
/// either the Blazor page or the domain service owning dispatch infrastructure.
/// </para>
/// </summary>
public interface IConsolidationDispatcher
{
    /// <summary>
    /// Dispatches the consolidation run to a K8s agent. If dispatch fails (no capacity,
    /// network error) the run remains <c>Queued</c> — startup rehydration retries on next restart.
    /// Never throws for runtime failures; logs and swallows them so the caller's status message
    /// is still shown. Throws <see cref="ArgumentNullException"/> if <paramref name="run"/> is null.
    /// </summary>
    /// <param name="run">The run to dispatch. Must not be null.</param>
    /// <param name="ct">Cancellation token.</param>
    Task DispatchRunAsync(ConsolidationRun run, CancellationToken ct);
}
