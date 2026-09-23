namespace CodingAgent.Pipeline;

/// <summary>
/// Authoritative source for all K8s Job naming formats used by the dispatch infrastructure.
/// Each method encodes one canonical naming contract; all dispatch paths must delegate here
/// rather than inlining format strings.
/// </summary>
/// <remarks>
/// A fourth format (<c>caa-chat-{8hex}</c>) is used by <c>ChatJobDispatcher</c> for ephemeral
/// chat pods; it is not included here because it derives from a freshly generated GUID rather
/// than a WorkItem ID, making it structurally incompatible with this deterministic factory.
/// </remarks>
public static class JobNameFactory
{
    /// <summary>
    /// Generates a deterministic K8s Job name for a brain/API-path WorkItem.
    /// Format: <c>caa-{first-8-chars-of-guid-no-dashes}</c> = 12 chars total.
    /// Used by <c>DispatchLifecycleService</c> in the API assembly.
    /// </summary>
    /// <param name="workItemId">The WorkItem ID.</param>
    public static string ForBrain(Guid workItemId) =>
        $"caa-{workItemId:N}"[..12]; // "caa-" (4) + 8 hex chars = 12 total
}
