using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Sends an agent call again after the model provider answered with a rate limit or an overload (#4289, #4295).
/// Used by <c>AgentStallMonitor</c> for pipeline runs and by the consolidation executors, which have no pipeline run.
/// </summary>
public static class ProviderBusyRetry
{
    /// <summary>
    /// How many times in a row a call is sent again after the model provider answered with a rate limit or an
    /// overload, counting only attempts that made no progress. When they are used up, the last result is
    /// returned as it is.
    /// </summary>
    public const int MaxConsecutiveRetries = 10;

    /// <summary>
    /// Upper bound on retries of one call, also when attempts in between made progress.
    /// </summary>
    public const int MaxRetriesPerCall = 30;

    /// <summary>
    /// Put in front of the prompt of a retried call that continues a session, so the agent knows its
    /// previous turn ended with a provider error and carries on instead of starting over.
    /// </summary>
    public const string RetryNote = "Your previous turn on this task was cut off by a model-provider error (rate limit or overload). Your work so far is in this conversation and in the workspace: continue from where you stopped instead of starting over.\n\n";

    /// <summary>
    /// True when the call failed because the model provider rate-limited or overloaded it: short-lived
    /// by definition, unlike a used-up quota or rejected credentials. A null result (an agent that
    /// reported nothing) is not a provider failure.
    /// </summary>
    public static bool IsProviderBusy(AgentResult result) =>
        result is { Success: false, ErrorCategory: AgentErrorCategory.ProviderRateLimit or AgentErrorCategory.ProviderOverload };

    /// <summary>
    /// Copies <paramref name="original"/> for a retry. A request that continues a session gets
    /// <see cref="RetryNote"/> in front of the original prompt; a fresh one is sent as it was.
    /// Always built from the original, so the note is never added twice.
    /// </summary>
    public static AgentRequest BuildRetryRequest(AgentRequest original) => new()
    {
        Prompt = original.UseResume || original.ResumeSessionId is not null
            ? RetryNote + original.Prompt
            : original.Prompt,
        WorkspacePath = original.WorkspacePath,
        Timeout = original.Timeout,
        UseResume = original.UseResume,
        ResumeSessionId = original.ResumeSessionId,
        ImagePaths = original.ImagePaths,
        EnvironmentVariables = original.EnvironmentVariables,
        EffortOverride = original.EffortOverride
    };

    /// <summary>
    /// Runs <paramref name="attempt"/> and, while its result is a provider rate limit or overload, waits
    /// <paramref name="retryDelay"/> and runs it again, up to <see cref="MaxConsecutiveRetries"/> times in a row
    /// and <see cref="MaxRetriesPerCall"/> times in all. An attempt that produced output tokens made progress and
    /// starts the consecutive count again. When the retries are used up, the last result is returned as it is.
    /// Any other result, failed or not, is returned at once.
    /// </summary>
    /// <param name="attempt">
    /// Sends one request to the agent. Receives the request to send, the number of retries so far in this call
    /// (0 for the first attempt) and the cancellation token.
    /// </param>
    /// <param name="request">The request of the first attempt; every retry is built from it.</param>
    /// <param name="retryDelay">How long to wait before each retry.</param>
    /// <param name="beforeRetry">
    /// Called before the wait of each retry with the busy result, the retries in a row so far and the retries in
    /// this call so far, both already counting the coming retry. May be null.
    /// </param>
    /// <param name="ct">Cancellation token for the attempts and for the wait.</param>
    /// <param name="timeProvider">
    /// Time source for the wait. Defaults to <see cref="TimeProvider.System"/>.
    /// Pass a fake/controllable provider in tests to eliminate wall-clock dependency.
    /// </param>
    public static async Task<AgentResult> ExecuteAsync(
        Func<AgentRequest, int, CancellationToken, Task<AgentResult>> attempt,
        AgentRequest request,
        TimeSpan retryDelay,
        Action<AgentResult, int, int>? beforeRetry,
        CancellationToken ct,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        ArgumentNullException.ThrowIfNull(request);

        timeProvider ??= TimeProvider.System;
        var attemptRequest = request;
        var consecutive = 0; // retries since the last attempt that made progress
        var total = 0; // all retries of this call
        while (true)
        {
            var result = await attempt(attemptRequest, total, ct);

            if (!IsProviderBusy(result))
                return result;

            // An attempt that produced output tokens moved the call forward, so the count starts again.
            if (result.Usage?.OutputTokens > 0)
                consecutive = 0;

            if (consecutive == MaxConsecutiveRetries || total == MaxRetriesPerCall)
                return result;

            consecutive++;
            total++;

            // TODO [WARNING] Tokens, cost, tool calls and usage details of every discarded busy attempt never reach the
            // run totals: callers call run.AccumulateTokenUsage only on the result returned here, while AccumulateAgentSession
            // still counts each attempt, so SessionCount and the PR cost text disagree (the span still carries the usage).
            // Accumulating here would race with parallel review agents, which merge their usage serially, so the fix needs a
            // decision: thread-safe accumulation, or folding the discarded usage into the returned result.

            beforeRetry?.Invoke(result, consecutive, total);

            // The wait is outside every attempt: nothing monitors the call while the provider recovers.
            await Task.Delay(retryDelay, timeProvider, ct);
            attemptRequest = BuildRetryRequest(request);
        }
    }
}
