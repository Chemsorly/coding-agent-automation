using System.Diagnostics;
using System.Threading;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using k8s.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Owns the per-session background watcher loop, idle-kill logic, and job-status polling
/// for chat session pods. Extracted from <see cref="ChatJobDispatcher"/> to separate
/// watcher-loop concerns from dispatch and termination concerns.
/// </summary>
internal interface IChatSessionWatcher
{
    /// <summary>
    /// Runs the background watcher loop for a single chat session until the job reaches
    /// a terminal state, the session is idle-killed, or cancellation is requested.
    /// </summary>
    /// <param name="jobName">K8s job name to monitor.</param>
    /// <param name="entry">The watcher entry for this session.</param>
    /// <param name="terminateCallback">
    /// Callback to invoke when the idle-kill path decides to terminate the session.
    /// Corresponds to <see cref="ChatJobDispatcher.TerminateChatSessionAsync"/>.
    /// </param>
    /// <param name="cleanupCallback">
    /// Callback to invoke when the session is ready to be cleaned up.
    /// Corresponds to <see cref="ChatJobDispatcher.CleanupSession"/>.
    /// </param>
    /// <param name="ct">Cancellation token linked to the watcher's own CTS and the host shutdown CTS.</param>
    Task WatchJobUntilTerminalAsync(
        string jobName,
        ChatJobDispatcher.WatcherEntry entry,
        Func<AgentId, CancellationToken, Task> terminateCallback,
        Action<AgentId, ChatJobDispatcher.WatcherEntry, string, string> cleanupCallback,
        CancellationToken ct);
}

/// <inheritdoc cref="IChatSessionWatcher"/>
internal sealed class ChatSessionWatcher : IChatSessionWatcher
{
    private readonly IKubernetesJobClient _jobClient;
    private readonly IChatHeartbeatTracker? _heartbeatTracker;
    private readonly DispatchServiceOptions _options;
    private readonly ILogger _logger;

    private const string TagAgentSelector = "agent_selector";

    public ChatSessionWatcher(
        IKubernetesJobClient jobClient,
        IChatHeartbeatTracker? heartbeatTracker,
        DispatchServiceOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _jobClient = jobClient;
        _heartbeatTracker = heartbeatTracker;
        _options = options;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task WatchJobUntilTerminalAsync(
        string jobName,
        ChatJobDispatcher.WatcherEntry entry,
        Func<AgentId, CancellationToken, Task> terminateCallback,
        Action<AgentId, ChatJobDispatcher.WatcherEntry, string, string> cleanupCallback,
        CancellationToken ct)
    {
        var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
        var idleTimeout = TimeSpan.FromSeconds(_options.ChatIdleTimeoutSeconds);
        // Compute once — constant for the watcher's lifetime.
        // Wake up no later than idleTimeout/3 so we react promptly to window-close.
        var pollInterval = TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, _options.ChatIdleTimeoutSeconds / 3)));

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // ── Circuit-based idle-kill check ──────────────────────────────────
                var lastHeartbeat = await ResolveLastHeartbeatAsync(jobName, entry).ConfigureAwait(false);
                if (lastHeartbeat is null)
                {
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                    continue;
                }

                var killResult = await TryTriggerIdleKillAsync(
                    jobName, entry, selectorEncoded, lastHeartbeat.Value, idleTimeout, pollInterval,
                    terminateCallback, cleanupCallback, ct).ConfigureAwait(false);
                if (killResult == IdleKillResult.KillTriggered) return;
                if (killResult == IdleKillResult.GuardFired) continue;

                var (job, readError) = await TryReadJobAsync(jobName).ConfigureAwait(false);
                // TODO [WARNING]: TryReadJobAsync forwards CancellationToken.None to ReadJobAsync.
                // If ct is cancelled while ReadJobAsync is in-flight (e.g. host shutdown with a slow K8s API),
                // the read will not be interrupted — the watcher cannot observe cancellation until the next
                // Task.Delay. Fix: forward ct into TryReadJobAsync (pre-existing design gap).
                // See review finding: DotNetSpecialist WARNING @ ChatSessionWatcher.cs:113.

                if (!readError && (job is null || ChatJobDispatcher.IsTerminal(job)))
                {
                    LogJobTermination(job, entry.JobName, entry.ClaimedPvc);
                    cleanupCallback(entry.AgentId, entry, selectorEncoded, "completed");
                    return;
                }

                try
                {
                    await Task.Delay(pollInterval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cleanupCallback(entry.AgentId, entry, selectorEncoded, "shutdown");
                    return;
                }
            }
            // ct was already cancelled when the while-condition was evaluated
            cleanupCallback(entry.AgentId, entry, selectorEncoded, "shutdown");
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation (e.g. _shutdownCts fired, or WatcherCts cancelled by
            // TerminateChatSessionAsync). Not an error — no Error log.
            cleanupCallback(entry.AgentId, entry, selectorEncoded, "shutdown");
        }
        catch (Exception ex)
        {
            _logger.Error(ex,
                "ChatSessionWatcher: watcher for job {JobName} faulted unexpectedly", jobName);
        }
        finally
        {
            // CleanupSession is idempotent via the entry.Cleaned CAS gate: every normal exit
            // path inside the try block already calls it, so this is a no-op for those paths.
            // The finally ensures cleanup executes even if _logger.Error (or any other
            // call in the catch body) throws.
            cleanupCallback(entry.AgentId, entry, selectorEncoded, "faulted");
        }
    }

    /// <summary>
    /// Resolves the authoritative last-heartbeat timestamp for the session.
    /// Returns <c>null</c> when Redis threw a transient fault — the caller must skip idle-kill.
    /// Returns a <see cref="DateTimeOffset"/> in all other cases.
    /// </summary>
    private async Task<DateTimeOffset?> ResolveLastHeartbeatAsync(string jobName, ChatJobDispatcher.WatcherEntry entry)
    {
        if (_heartbeatTracker is not null)
        {
            var (redisAvailable, redisHeartbeat) = await _heartbeatTracker
                .TryGetRedisHeartbeatAsync(jobName, entry.AgentId).ConfigureAwait(false);
            if (!redisAvailable)
                return null;
            return redisHeartbeat ?? new DateTimeOffset(
                Interlocked.Read(ref entry.LastClientHeartbeatTicks), TimeSpan.Zero);
        }

        return new DateTimeOffset(
            Interlocked.Read(ref entry.LastClientHeartbeatTicks), TimeSpan.Zero);
    }

    /// <summary>
    /// Evaluates whether the session has been idle long enough to terminate, and triggers
    /// termination if so — guarded by a <see cref="Interlocked.CompareExchange"/> single-fire lock.
    /// </summary>
    private async Task<IdleKillResult> TryTriggerIdleKillAsync(
        string jobName,
        ChatJobDispatcher.WatcherEntry entry,
        string selectorEncoded,
        DateTimeOffset lastHeartbeat,
        TimeSpan idleTimeout,
        TimeSpan pollInterval,
        Func<AgentId, CancellationToken, Task> terminateCallback,
        Action<AgentId, ChatJobDispatcher.WatcherEntry, string, string> cleanupCallback,
        CancellationToken ct)
    {
        var idleSince = DateTimeOffset.UtcNow - lastHeartbeat;
        if (idleSince <= idleTimeout)
            return IdleKillResult.NotIdle;

        // Only fire idle-kill if TerminateChatSessionAsync hasn't already been called.
        if (Interlocked.CompareExchange(ref entry.Terminating, 1, 0) != 0)
        {
            _logger.Debug(
                "ChatSessionWatcher: idle-kill skipped for {JobName} — termination already in progress",
                jobName);
            // TODO [WARNING]: OCE is intentionally swallowed here — if ct is cancelled during this
            // delay, the cancellation signal is dropped and GuardFired is returned, letting the
            // while (!ct.IsCancellationRequested) condition exit the loop on the next iteration.
            // This is one extra evaluation rather than a direct exit. The behavior is correct for
            // the current use but silently discarding OCE on an explicit ct parameter is a code smell.
            // See review finding: DotNetSpecialist WARNING @ ChatSessionWatcher.cs:161.
            // ct cancelled — let the while-condition exit the loop on the next iteration.
            try { await Task.Delay(pollInterval, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
            return IdleKillResult.GuardFired;
        }

        _logger.Warning(
            "ChatSessionWatcher: chat pod {JobName} idle for {IdleSeconds:F0}s (threshold={Threshold}s) — terminating",
            jobName, idleSince.TotalSeconds, _options.ChatIdleTimeoutSeconds);
        // TODO [WARNING]: terminateCallback in production wiring is TerminateChatSessionAsync, which
        // awaits entry.WatcherTask.WaitAsync(gracePeriod). This method runs *inside* entry.WatcherTask,
        // so the watcher task awaits itself and can never complete cleanly — every idle-kill goes down
        // the force-delete path after ChatTerminationGracePeriodSeconds. Behavior is pre-existing
        // (identical to the original inline WatchJobUntilTerminalAsync). To fix, the idle-kill path
        // should invoke K8s delete + CleanupSession directly instead of routing through terminate.
        // See review finding: Correctness WARNING @ ChatSessionWatcher.cs:194.
        await terminateCallback(entry.AgentId, CancellationToken.None).ConfigureAwait(false);
        // CleanupSession is gated by Interlocked.CompareExchange(ref entry.Cleaned, 1, 0),
        // so if force-delete already ran it, this is a safe no-op.
        cleanupCallback(entry.AgentId, entry, selectorEncoded, "shutdown");
        return IdleKillResult.KillTriggered;
    }

    private async Task<(V1Job? job, bool readError)> TryReadJobAsync(string jobName)
    {
        try
        {
            var job = await _jobClient.ReadJobAsync(jobName, _options.Namespace, CancellationToken.None)
                .ConfigureAwait(false);
            return (job, false);
        }
        catch (Exception ex) when (ChatJobDispatcher.IsNotFound(ex))
        {
            _logger.Information(ex,
                "ChatSessionWatcher: job {JobName} no longer exists in K8s — treating as terminal",
                jobName);
            return (null, false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatSessionWatcher: transient ReadJobAsync failure for {JobName} (will retry): {ErrorMessage}",
                jobName, ex.Message);
            return (null, true);
        }
    }

    private void LogJobTermination(V1Job? job, string jobName, string? claimedPvc)
    {
        if (job is null)
        {
            _logger.Warning(
                "ChatSessionWatcher: job {JobName} not found (externally deleted) — releasing PVC {Pvc}",
                jobName, claimedPvc ?? "none");
        }
        else
        {
            var isFailed = job.Status?.Conditions?.Any(
                c => c.Type == "Failed" && c.Status == "True") == true;

            if (isFailed)
                _logger.Warning(
                    "ChatSessionWatcher: job {JobName} failed — PVC {Pvc} released",
                    jobName, claimedPvc ?? "none");
            else
                _logger.Information(
                    "ChatSessionWatcher: job {JobName} completed — PVC {Pvc} released",
                    jobName, claimedPvc ?? "none");
        }
    }

    /// <summary>
    /// Tri-state result from <see cref="TryTriggerIdleKillAsync"/>.
    /// </summary>
    private enum IdleKillResult
    {
        /// <summary>The session is not yet idle — fall through to the normal job-status poll.</summary>
        NotIdle,
        /// <summary>Idle-kill was triggered — caller must <c>return</c>.</summary>
        KillTriggered,
        /// <summary>CAS guard fired — caller must <c>continue</c>.</summary>
        GuardFired
    }
}
