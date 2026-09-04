using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Minimal abstraction over the work-item API required by the queue sweep in
/// <see cref="Services.PipelineLoopService"/>. Defined in the Pipeline project to avoid
/// a circular reference between Pipeline and Api.Client.
/// <para>
/// <c>IPipelineApiWorkItemClient</c> (in CodingAgentWebUI.Api.Client) implements this interface,
/// so the Scheduler can inject an <c>IPipelineApiWorkItemClient</c> wherever an
/// <c>IWorkItemSweepClient</c> is expected.
/// </para>
/// </summary>
public interface IWorkItemSweepClient
{
    /// <summary>
    /// Returns Pending WorkItems, up to <paramref name="maxResults"/>.
    /// </summary>
    Task<IReadOnlyList<PendingWorkItemDto>> GetPendingAsync(int maxResults = 50, CancellationToken ct = default);

    /// <summary>
    /// Posts a status update for the given WorkItem (e.g. Cancelled).
    /// Throws <see cref="System.Net.Http.HttpRequestException"/> on non-2xx responses.
    /// </summary>
    Task PostStatusAsync(Guid workItemId, WorkItemStatusUpdate request, CancellationToken ct = default);
}
