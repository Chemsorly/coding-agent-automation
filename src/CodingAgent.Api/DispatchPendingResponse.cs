namespace CodingAgent.Api;

/// <summary>
/// Response body for <c>POST /api/work-items/{id}/dispatch</c> (the Scheduler-facing pending-dispatch endpoint).
/// Always returned with HTTP 200; the <see cref="Dispatched"/> flag distinguishes success from deferred.
/// </summary>
/// <param name="Dispatched">
/// <see langword="true"/> when the item was successfully dispatched (K8s Job created, WorkItem=Dispatched).
/// <see langword="false"/> when the item was not dispatched for an expected reason — the Scheduler should
/// stop dispatching this selector for the current cycle.
/// </param>
/// <param name="Reason">
/// Closed set of strings indicating why the item was not dispatched when <see cref="Dispatched"/> is
/// <see langword="false"/>:
/// <list type="bullet">
///   <item><c>concurrency_limit</c> — the concurrency limit for this selector is reached.</item>
///   <item><c>not_pending</c> — the item is no longer in Pending state.</item>
///   <item><c>no_template</c> — no job template matches the item's agent selector.</item>
/// </list>
/// <c>none</c> when <see cref="Dispatched"/> is <see langword="true"/>.
/// </param>
internal sealed record DispatchPendingResponse(bool Dispatched, string Reason);
