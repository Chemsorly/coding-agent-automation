namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Carries the result of a single retry-loop outcome handler back to the dispatcher in
/// <see cref="Services.QualityGateExecutor"/>. Replaces the anonymous 3-tuple that
/// <c>RunFixAgentIterationAsync</c> previously returned.
/// </summary>
/// <param name="ShouldBreak">
/// When <see langword="true"/>, the retry loop must exit immediately after applying mutations.
/// </param>
/// <param name="ShouldContinue">
/// When <see langword="true"/>, the retry loop must skip quality-gate validation and proceed
/// directly to the next iteration. Mutually exclusive with <see cref="ShouldBreak"/>.
/// </param>
/// <param name="ConsecutiveTransientRetries">
/// Updated value of the consecutive-transient counter. The caller assigns this back to its
/// loop variable after each iteration.
/// </param>
/// <param name="RetryCountDelta">
/// Amount by which <c>run.RetryCount</c> should be incremented. Applied once in the shared
/// dispatcher after the handler returns, centralizing all <c>RetryCount</c> mutations.
/// </param>
/// <param name="EndedByProviderFailure">
/// When <see langword="true"/>, the loop stops because the fix agent's model provider failed (a permanent
/// provider failure, or the consecutive-transient cap), not because the agent ran out of fix attempts.
/// Only set together with <see cref="ShouldBreak"/>.
/// </param>
internal record RetryDecision(
    bool ShouldBreak,
    bool ShouldContinue,
    int ConsecutiveTransientRetries,
    int RetryCountDelta,
    bool EndedByProviderFailure = false);
