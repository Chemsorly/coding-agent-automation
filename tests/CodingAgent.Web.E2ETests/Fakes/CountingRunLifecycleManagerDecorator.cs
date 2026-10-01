using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// Decorator around <see cref="IRunLifecycleManager"/> that counts how many times
/// each mutating method has been called. Used in E2E tests that need to verify
/// server-side idempotency guarantees (e.g., that a double-click UI guard prevents
/// two concurrent <c>CancelRunAsync</c> calls from both reaching the server).
///
/// <para>
/// The counters are plain <c>int</c> fields incremented with
/// <see cref="System.Threading.Interlocked.Increment"/> for thread-safety.
/// Reset them between tests with <see cref="ResetCounters"/>.
/// </para>
/// </summary>
public sealed class CountingRunLifecycleManagerDecorator : IRunLifecycleManager
{
    private readonly IRunLifecycleManager _inner;

    private int _cancelRunCallCount;
    private int _failRunCallCount;
    private int _completeRunCallCount;

    public CountingRunLifecycleManagerDecorator(IRunLifecycleManager inner)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
    }

    /// <summary>Total number of times <see cref="CancelRunAsync"/> has been invoked.</summary>
    public int CancelRunCallCount => _cancelRunCallCount;

    /// <summary>Total number of times <see cref="FailRunAsync"/> (or <see cref="FailRunWithLabelAsync"/>) has been invoked.</summary>
    public int FailRunCallCount => _failRunCallCount;

    /// <summary>Total number of times <see cref="CompleteRunAsync"/> has been invoked.</summary>
    public int CompleteRunCallCount => _completeRunCallCount;

    /// <summary>Resets all counters to zero. Call this between tests via <c>ResetAll()</c>.</summary>
    public void ResetCounters()
    {
        System.Threading.Interlocked.Exchange(ref _cancelRunCallCount, 0);
        System.Threading.Interlocked.Exchange(ref _failRunCallCount, 0);
        System.Threading.Interlocked.Exchange(ref _completeRunCallCount, 0);
    }

    public Task<PipelineRun?> CancelRunAsync(RunId runId, CancellationToken ct, string? failureReason = null)
    {
        System.Threading.Interlocked.Increment(ref _cancelRunCallCount);
        return _inner.CancelRunAsync(runId, ct, failureReason);
    }

    public Task<PipelineRun?> FailRunAsync(RunId runId, string failureReason, CancellationToken ct, FailureReason? failureReasonEnum = null)
    {
        System.Threading.Interlocked.Increment(ref _failRunCallCount);
        return _inner.FailRunAsync(runId, failureReason, ct, failureReasonEnum);
    }

    public Task<PipelineRun?> FailRunWithLabelAsync(RunId runId, string failureReason, string? resolvedFinalLabel, CancellationToken ct, FailureReason? failureReasonEnum = null)
    {
        System.Threading.Interlocked.Increment(ref _failRunCallCount);
        return _inner.FailRunWithLabelAsync(runId, failureReason, resolvedFinalLabel, ct, failureReasonEnum);
    }

    public Task<PipelineRun?> CompleteRunAsync(RunId runId, WorkItemStatus terminalStatus, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        System.Threading.Interlocked.Increment(ref _completeRunCallCount);
        return _inner.CompleteRunAsync(runId, terminalStatus, ct, errorMessage, failureReason);
    }

    public Task AgentAcceptedRunAsync(RunId runId, AgentId agentId, IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderConfigId, ProviderConfigId repoProviderConfigId,
        PipelineRunType runType, CancellationToken ct)
        => _inner.AgentAcceptedRunAsync(runId, agentId, issueIdentifier, issueProviderConfigId,
            repoProviderConfigId, runType, ct);

    public Task TransitionWorkItemToFailedAsync(RunId runId, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
        => _inner.TransitionWorkItemToFailedAsync(runId, ct, errorMessage, failureReason);
}
