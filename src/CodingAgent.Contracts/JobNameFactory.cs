namespace CodingAgent.Pipeline;

/// <summary>
/// Authoritative source for all K8s Job naming formats used by the dispatch infrastructure.
/// Each method encodes one canonical naming contract; all dispatch paths must delegate here
/// rather than inlining format strings.
/// </summary>
/// <remarks>
/// Two naming formats exist because each dispatch path was implemented independently:
/// <list type="bullet">
///   <item><see cref="ForWorkItem"/> — regular agent jobs (DispatchLoop, removed in #2322)</item>
///   <item><see cref="ForBrain"/> — brain/API-path jobs (DispatchLifecycleService)</item>
/// </list>
/// The formats are intentionally preserved as-is. Changing any format string would orphan
/// in-flight K8s Jobs, breaking reconciliation. A fourth format (<c>caa-chat-{8hex}</c>)
/// is used by <c>ChatJobDispatcher</c> for ephemeral chat pods; it is not included here
/// because it derives from a freshly generated GUID rather than a WorkItem ID, making it
/// structurally incompatible with this deterministic factory.
/// </remarks>
public static class JobNameFactory
{
    /// <summary>
    /// Generates a deterministic K8s Job name for a regular agent WorkItem.
    /// Format: <c>caa-agent-{first-11-chars-of-guid-no-dashes}</c> = 21 chars total.
    /// </summary>
    /// <remarks>
    /// <c>DispatchLoop</c> (removed in issue #2322) was the only production caller.
    /// <c>ReconciliationLoop</c> still uses this format as a null-fallback for
    /// <c>WorkItem.K8sJobName</c> on legacy rows created before the field was persisted.
    /// </remarks>
    /// <param name="workItemId">The WorkItem ID.</param>
    public static string ForWorkItem(Guid workItemId) =>
        $"caa-agent-{workItemId:N}"[..21]; // "caa-agent-" (10) + 11 hex chars = 21 total

    /// <summary>
    /// Generates a deterministic K8s Job name for a brain/API-path WorkItem.
    /// Format: <c>caa-{first-8-chars-of-guid-no-dashes}</c> = 12 chars total.
    /// Used by <c>DispatchLifecycleService</c> in the API assembly.
    /// </summary>
    /// <param name="workItemId">The WorkItem ID.</param>
    public static string ForBrain(Guid workItemId) =>
        $"caa-{workItemId:N}"[..12]; // "caa-" (4) + 8 hex chars = 12 total
}
