using CodingAgent.Api.Client;
using CodingAgent.Orchestration.Redis;
using CodingAgent.Pipeline;
using StackExchange.Redis;
using System.Text.Json;
using ILeaderGate = CodingAgent.Pipeline.Interfaces.ILeaderGate;

namespace CodingAgent.Scheduler;

/// <summary>
/// Commands that can be relayed from a non-leader pod to the leader.
/// </summary>
public enum LoopCommand
{
    Start,
    Stop,
    Resume
}

/// <summary>
/// Result returned by <see cref="ILoopCommandRelay.SendAsync"/>.
/// </summary>
/// <param name="Success">Whether the command was handled by the leader.</param>
/// <param name="StartResult">
/// Populated when <see cref="LoopCommand.Start"/> was issued and the leader responded.
/// </param>
/// <param name="Error">Error message on timeout or failure.</param>
public record LoopCommandResult(bool Success, LoopStartResultDto? StartResult = null, string? Error = null);

/// <summary>
/// Relay interface used by endpoint handlers on non-leader pods.
/// </summary>
public interface ILoopCommandRelay
{
    /// <summary>
    /// Writes the command to Redis and polls for the leader's result.
    /// Returns a failure result on timeout.
    /// </summary>
    Task<LoopCommandResult> SendAsync(LoopCommand command, CancellationToken ct);
}

// ── Internal types for Redis serialization ──────────────────────────────────

internal sealed record LoopCommandMessage(string Id, LoopCommand Command);

internal sealed record LoopCommandResultMessage(bool Success, LoopStartResultDto? StartResult, string? Error);

// ── Real relay (used when Redis is configured) ───────────────────────────────

/// <summary>
/// Sends a loop command to the leader pod via Redis.
/// The non-leader writes <c>scheduler:loop-command</c> and polls
/// <c>scheduler:loop-command-result:{id}</c> for up to <paramref name="timeout"/>.
/// </summary>
public sealed class LoopCommandRelay : ILoopCommandRelay
{
    internal const string CommandKey = "scheduler:loop-command";
    internal const string ResultKeyPrefix = "scheduler:loop-command-result:";
    private const string TimeoutError = "The scheduler leader did not confirm the command within 10 s.";

    private readonly IRedisStore _store;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _pollInterval;

    public LoopCommandRelay(
        IRedisStore store,
        // NOTE (issue #3552): leaderGate is accepted but never stored or used. The endpoint's
        // ShouldRunLocally guard already prevents SendAsync from being called on the leader pod,
        // so this has no functional effect today — but the unused parameter is misleading to future
        // maintainers who may assume it influences relay behaviour (see .NET Specialist review).
        ILeaderGate? leaderGate = null,
        TimeSpan? timeout = null,
        TimeSpan? pollInterval = null)
    {
        _store = store;
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
    }

    /// <inheritdoc/>
    public async Task<LoopCommandResult> SendAsync(LoopCommand command, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString("N");
        var message = new LoopCommandMessage(id, command);
        var json = JsonSerializer.Serialize(message, PipelineJsonOptions.Default);

        await _store.SetAsync(CommandKey, json, TimeSpan.FromSeconds(60), When.Always);

        var resultKey = ResultKeyPrefix + id;
        var deadline = DateTimeOffset.UtcNow + _timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            // NOTE (issue #3552): If ct is cancelled during Task.Delay, OperationCanceledException
            // propagates out of SendAsync uncaught. The endpoint callers do not catch it either,
            // so ASP.NET returns a 500. For the stop command this happens after config persistence
            // has already succeeded, so state is durable, but the 500 response misleads the caller.
            // Under normal operation (no mid-request host shutdown) this path is unreachable.
            // Consider wrapping Task.Delay in a try/catch OperationCanceledException to return
            // a graceful LoopCommandResult(false, Error: "Request cancelled.") instead (see
            // Correctness review).
            await Task.Delay(_pollInterval, ct);

            // NOTE (issue #3552): ct is not propagated to _store.GetAsync. Any in-flight GetAsync call
            // started just before cancellation runs to completion without observing the token. This
            // is a best-practice deviation; the call is fast enough that the impact is negligible in
            // practice (see .NET Specialist review).
            var resultJson = await _store.GetAsync(resultKey);
            if (resultJson is not null)
            {
                var result = JsonSerializer.Deserialize<LoopCommandResultMessage>(
                    resultJson, PipelineJsonOptions.Lenient);
                if (result is not null)
                    return new LoopCommandResult(result.Success, result.StartResult, result.Error);
            }
        }

        return new LoopCommandResult(false, Error: TimeoutError);
    }
}

// ── Null-object relay (used when Redis is not configured) ────────────────────

/// <summary>
/// Null-object implementation of <see cref="ILoopCommandRelay"/> registered when Redis is absent.
/// The endpoint guard (<c>store is null → local path</c>) ensures <see cref="SendAsync"/> is
/// never called in practice; throwing here surfaces any future bug that bypasses that guard.
/// </summary>
internal sealed class NullLoopCommandRelay : ILoopCommandRelay
{
    public Task<LoopCommandResult> SendAsync(LoopCommand command, CancellationToken ct)
        => throw new InvalidOperationException(
            "LoopCommandRelay.SendAsync was called but Redis is not configured. " +
            "This indicates a bug: the endpoint should have taken the local path.");
}
