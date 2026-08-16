using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Extension methods for <see cref="IOrchestratorRunService"/> used internally by the dispatch layer.
/// </summary>
internal static class OrchestratorRunServiceExtensions
{
    /// <summary>
    /// Corrects the in-memory <see cref="Pipeline.Models.PipelineRun"/> <c>StartedAt</c> to the actual
    /// dispatch time (BUG-14 fix). Without this correction, <c>StartedAt</c> reflects the
    /// preparation/enqueue time, which can be hours earlier for queued work, inflating the
    /// Duration shown in the UI.
    /// </summary>
    /// <param name="runService">
    /// The run service. May be <c>null</c> — the call is a no-op when <c>null</c> (K8s path omits
    /// <c>RunService</c> in some test setups via <see cref="DispatchServiceCoreDependencies"/>).
    /// </param>
    /// <param name="runId">
    /// The run identifier. Both current callers guarantee a non-empty value before calling this method.
    /// Note: when <paramref name="runService"/> is <c>null</c>, <c>GetRun</c> is never invoked and the
    /// call is a silent no-op regardless of <paramref name="runId"/>'s value — no
    /// <see cref="ArgumentException"/> is thrown in that case.
    /// </param>
    /// <param name="dispatchedAt">The actual dispatch time to set as <c>StartedAt</c>.</param>
    // TODO: The doc comment for runId previously claimed an ArgumentException is thrown for null/empty
    // runId via the RunId implicit string conversion, but that guarantee only holds when runService is
    // non-null. When runService is null the method short-circuits before GetRun is called, so an invalid
    // runId silently no-ops instead of throwing. Consider adding ArgumentException.ThrowIfNullOrEmpty(runId)
    // as an explicit guard to make the contract unconditional and catch caller mistakes early. (#2065)
    public static void PostDispatchTimingCorrection(
        this IOrchestratorRunService? runService,
        string runId,
        DateTimeOffset dispatchedAt)
    {
        runService?.GetRun(runId)?.ResetStartedAt(dispatchedAt);
    }
}
