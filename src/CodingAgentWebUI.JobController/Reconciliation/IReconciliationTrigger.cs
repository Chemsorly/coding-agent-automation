namespace CodingAgentWebUI.JobController.Reconciliation;

/// <summary>
/// Allows external components to request an immediate reconciliation cycle
/// without waiting for the regular 30-second poll interval.
/// </summary>
/// <remarks>
/// Implemented by <see cref="ReconciliationService"/>.
/// Injecting this interface (rather than the concrete service) keeps
/// <see cref="CodingAgentWebUI.JobController.Dispatch.DispatchLoop"/> and
/// <see cref="CodingAgentWebUI.JobController.Dispatch.ConsolidationDispatchLoop"/>
/// decoupled from the reconciliation implementation and makes the trigger
/// easily mockable in unit tests.
/// </remarks>
public interface IReconciliationTrigger
{
    /// <summary>
    /// Signals <see cref="ReconciliationService"/> to begin a reconciliation cycle
    /// as soon as possible rather than waiting for the next scheduled poll.
    /// The call is non-blocking and idempotent: multiple calls before the service
    /// wakes collapse into a single extra cycle.
    /// Has no effect when <see cref="ReconciliationService"/> is not the leader or
    /// is not running.
    /// </summary>
    void RequestImmediateCycle();
}
