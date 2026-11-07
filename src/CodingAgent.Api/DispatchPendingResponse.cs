namespace CodingAgent.Api;

/// <summary>
/// Response body for <c>POST /api/work-items/{id}/dispatch</c> (the Scheduler-facing pending-dispatch endpoint).
/// Always returned with HTTP 200; the <see cref="Dispatched"/> flag distinguishes success from deferred.
/// </summary>
/// <param name="Dispatched">
/// <see langword="true"/> when the item was successfully dispatched (K8s Job created, WorkItem=Dispatched).
/// <see langword="false"/> when the item was not dispatched for an expected reason: it stays Pending with the hold.
/// </param>
/// <param name="Reason">
/// Closed set of strings indicating why the item was not dispatched when <see cref="Dispatched"/> is
/// <see langword="false"/> (spec 056 "Hold Reasons and Texts"):
/// <list type="bullet">
///   <item><c>global_concurrency_limit</c> — the limit for all agents together is reached (spec 057).</item>
///   <item><c>concurrency_limit</c> — the limit of the item's job template is reached.</item>
///   <item><c>pvc_unavailable</c> — no Kiro credential PVC is free.</item>
///   <item><c>credential_paused</c> — the route's credential is paused.</item>
///   <item><c>no_route</c>, <c>profile_unavailable</c>, <c>no_tool</c>, <c>no_template</c> — the route cannot run.</item>
///   <item><c>not_pending</c> — the item is no longer in Pending state.</item>
/// </list>
/// <c>none</c> when <see cref="Dispatched"/> is <see langword="true"/>.
/// </param>
/// <param name="HoldText">Spec 056: the hold's text, as the Work page shows it; null when dispatched or not pending.</param>
/// <param name="TemplateKey">
/// Spec 056 DD9: the job template key the item resolved to (normalized, as the stored selector); the Scheduler stops it
/// for the cycle on <c>concurrency_limit</c> and <c>pvc_unavailable</c>. Null when no route resolved.
/// </param>
/// <param name="Credential">
/// Spec 056 DD9: the credential the item resolved to; the Scheduler stops it for the cycle on <c>credential_paused</c>.
/// </param>
internal sealed record DispatchPendingResponse(
    bool Dispatched,
    string Reason,
    string? HoldText = null,
    string? TemplateKey = null,
    string? Credential = null);
