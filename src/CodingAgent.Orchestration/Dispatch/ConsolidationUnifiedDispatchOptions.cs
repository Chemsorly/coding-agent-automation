namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Configuration options for the consolidation unified-dispatch flag.
/// Bound from the <c>Consolidation:UnifiedDispatch</c> configuration section.
/// </summary>
/// <remarks>
/// When <see cref="Enabled"/> is <c>false</c> (default), consolidation runs are dispatched
/// synchronously via <c>POST /api/work-items/dispatch</c> (legacy path, creates WorkItem as
/// <c>Dispatched</c> immediately). When <c>true</c>, consolidation runs are enqueued as
/// <c>Pending</c> WorkItems via <c>POST /api/work-items</c> and claimed by the poller, which
/// applies RunType tier ordering (#2563) before creating the K8s Job.
/// <para>
/// The flag defaults <c>false</c> so it ships inert — no Helm values change is required.
/// Enable in a test environment once #2563 (RunType tier ordering at dispatch) is deployed.
/// </para>
/// </remarks>
public sealed class ConsolidationUnifiedDispatchOptions
{
    /// <summary>
    /// Configuration section name: <c>Consolidation:UnifiedDispatch</c>.
    /// </summary>
    public const string SectionName = "Consolidation:UnifiedDispatch";

    /// <summary>
    /// When <c>true</c>, consolidation runs are enqueued as <c>Pending</c> WorkItems (unified
    /// dispatch path). When <c>false</c> (default), consolidation runs use the legacy
    /// synchronous dispatch path (<c>Dispatched</c> directly).
    /// </summary>
    public bool Enabled { get; set; } = false;
}
