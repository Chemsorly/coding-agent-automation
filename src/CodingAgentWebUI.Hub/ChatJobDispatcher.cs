using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Kubernetes;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Redis;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Dispatches on-demand ephemeral chat pods as K8s Jobs, polls for agent connection,
/// maintains per-session background watchers for metric housekeeping on job terminal,
/// and handles terminate/cleanup on navigate-away.
///
/// <para>
/// Leader election was removed in Spec 049. All replicas can dispatch independently;
/// the K8s <see cref="CheckForExistingJob"/> guard (live <c>ListJobsAsync</c> query)
/// prevents duplicate pods for the same selector. PVC availability is read from K8s
/// job labels at dispatch time — no in-memory PVC pool required.
/// </para>
///
/// <para>
/// Session state is intentionally minimal: only a watcher task and a double-cleanup
/// guard are tracked in-memory (via <see cref="_activeWatchers"/>). Jobs active before
/// this process started drain via <c>ActiveDeadlineSeconds</c> — no startup recovery.
/// </para>
/// </summary>
/// <remarks>
/// Requirements: Req 2, Req 3, Req 12, Req 13, Req 16, Req 17, Req 18.
/// Lives in the web project so it can reference <see cref="AgentHub"/> and
/// <see cref="IAgentHubClient"/> without creating a circular project dependency.
/// </remarks>
public sealed partial class ChatJobDispatcher : IHostedService, IAsyncDisposable, IChatJobDispatcher
{
    private readonly IKubernetesJobClient _jobClient;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly JobTemplateStore _templateStore;
    private readonly IAgentRegistryService _registry;
    private readonly DispatchServiceOptions _options;
    private readonly ILogger _logger;
    // Optional — null in local dev without Redis. When non-null, heartbeat timestamps are
    // written here so any API replica can check liveness regardless of which replica the
    // keepalive POST lands on.
    private readonly IRedisStore? _redis;

    private const string TagAgentSelector = "agent_selector";
    private const string LabelChatSessionId = "caa/chat-session-id";
    private const string TagOutcome = "outcome";

    // ── Minimal session tracking ──────────────────────────────────────────────
    // Keyed by jobName (== agentId for chat pods — see invariant note on TerminateChatSessionAsync).
    // Stores the background watcher task and a double-cleanup guard flag (0 = uncleaned, 1 = cleaned).
    // The flag prevents concurrent watcher + TerminateChatSessionAsync from double-decrementing metrics
    // and double-calling DeleteJobAsync when both paths race to see a terminal job.
    private readonly ConcurrentDictionary<string, WatcherEntry> _activeWatchers = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    // Idempotency guard for StopAsync: 0 = not yet stopped, 1 = stop in progress or completed.
    // Prevents the double StopAsync call (IHostedService.StopAsync + DisposeAsync) from
    // invoking _shutdownCts.CancelAsync() twice and racing with _shutdownCts.Dispose().
    private int _stopped;

    // Completion signal: set when StopAsync finishes its entire body (including CancelAsync and
    // watcher drain). DisposeAsync awaits this before calling _shutdownCts.Dispose(), ensuring
    // that even if DisposeAsync is called concurrently with an in-progress StopAsync, Dispose
    // never races with CancelAsync.
    private readonly TaskCompletionSource _stopCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class WatcherEntry
    {
        public Task WatcherTask = Task.CompletedTask; // assigned after construction; see RegisterWatcher
        public readonly string AgentId;  // dict key; == jobName in production, may differ in tests
        public readonly string JobName;
        public readonly string NormalizedSelector;
        public readonly string? ClaimedPvc;
        public readonly DateTimeOffset StartedAt;
        public readonly CancellationTokenSource WatcherCts; // disposed in CleanupSession
        public int Cleaned; // 0 = not yet cleaned; 1 = cleanup done. Used with Interlocked.

        // Circuit-based lifecycle: tracks last client keepalive. Initialised to StartedAt so the
        // idle clock starts from dispatch, not from an arbitrary epoch. Updated via
        // RecordClientHeartbeat. Read by WatchJobUntilTerminalAsync to detect window-closed.
        public long LastClientHeartbeatTicks; // written/read with Interlocked for thread safety

        // Guard: 0 = not yet terminating; 1 = termination in progress or complete.
        // Used by the idle-kill path to avoid launching a second TerminateChatSessionAsync
        // when an explicit TerminateChatSessionAsync is already in flight.
        public int Terminating;

        // Guard: 0 = CancelChat not yet sent; 1 = already sent.
        // Prevents double-CancelChat when an explicit TerminateChatSessionAsync races with an
        // in-flight idle-kill that already called TrySendCancelChatAsync.
        public int CancelSent;

        public WatcherEntry(string agentId, string jobName, string normalizedSelector, string? claimedPvc,
            DateTimeOffset startedAt, CancellationTokenSource watcherCts)
        {
            AgentId = agentId;
            JobName = jobName;
            NormalizedSelector = normalizedSelector;
            ClaimedPvc = claimedPvc;
            StartedAt = startedAt;
            WatcherCts = watcherCts;
            LastClientHeartbeatTicks = startedAt.UtcTicks;
        }
    }

    public ChatJobDispatcher(
        IKubernetesJobClient jobClient,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        JobTemplateStore templateStore,
        IAgentRegistryService registry,
        DispatchServiceOptions options,
        ILogger logger,
        IRedisStore? redis = null)
    {
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _jobClient = jobClient;
        _hubContext = hubContext;
        _templateStore = templateStore;
        _registry = registry;
        _options = options;
        _logger = logger;
        _redis = redis;
    }

    // ─── DispatchChatPodAsync ──────────────────────────────────────────────────

    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentSelector);
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Dispatch");

        var (normalized, selectorLabelValue) = NormalizeSelector(agentSelector);
        activity?.SetTag(TagAgentSelector, normalized);

        // Query all active chat jobs — used for both double-dispatch guard and PVC availability.
        // Querying live K8s state makes this replica-safe without a leader gate.
        var allChatJobs = await _jobClient.ListJobsAsync(
            _options.Namespace, LabelChatSessionId, cancellationToken);

        var activeChatJobs = allChatJobs.Items?
            .Where(j => !IsTerminal(j))
            .ToList() ?? [];

        var template = _templateStore.Resolve(normalized)
            ?? throw new InvalidOperationException($"No template for selector '{normalized}'");

        var jobName = $"caa-chat-{Guid.NewGuid().ToString("N")[..8]}";
        var dispatchId = Guid.NewGuid();
        var dispatchStart = DateTimeOffset.UtcNow;

        var claimedPvc = ClaimPvcForKiroAgent(template.ProviderType, activeChatJobs);

        await BuildAndSubmitChatJobAsync(normalized, selectorLabelValue, model, effort, jobName, dispatchId, claimedPvc, template, cancellationToken);

        _logger.Information(
            "ChatJobDispatcher: dispatched chat pod {JobName} for selector {AgentSelector} (dispatchId={DispatchId}, pvc={Pvc})",
            jobName, normalized, dispatchId, claimedPvc ?? "none");

        activity?.SetTag("dispatch_id", dispatchId.ToString());
        activity?.SetTag("job_name", jobName);
        activity?.SetTag("model", model ?? "auto");
        activity?.SetTag("effort", effort ?? "auto");
        activity?.SetTag("provider_type", template.ProviderType);

        return await PollForAgentConnectionAsync(
            dispatchId, jobName, claimedPvc, normalized, selectorLabelValue,
            dispatchStart, activity, cancellationToken);
    }

    private (string normalized, string selectorLabelValue) NormalizeSelector(string agentSelector)
    {
        var normalized = JobTemplateStore.NormalizeLabels(agentSelector);
        var selectorLabelValue = normalized.Replace(',', '_');

        if (!K8sLabelValuePattern().IsMatch(selectorLabelValue))
            throw new ArgumentException(
                $"Agent selector '{agentSelector}' produces an invalid k8s label value '{selectorLabelValue}'. " +
                "Label values must match [a-zA-Z0-9._-] and be ≤63 characters.");

        return (normalized, selectorLabelValue);
    }

    private string? ClaimPvcForKiroAgent(string providerType, List<V1Job> activeChatJobs)
    {
        if (!IsKiroAgent(providerType))
            return null;

        var claimedByActiveJobs = activeChatJobs
            .Select(j =>
            {
                var labels = j.Metadata?.Labels;
                return labels is not null && labels.TryGetValue("caa/claimed-pvc", out var p) ? p : null;
            })
            .Where(p => p is not null)
            .ToHashSet(StringComparer.Ordinal)!;

        var claimedPvc = _options.KiroPvcPool.FirstOrDefault(p => !claimedByActiveJobs.Contains(p));
        if (claimedPvc is null)
            throw new NoPvcAvailableException();

        return claimedPvc;
    }

    private async Task BuildAndSubmitChatJobAsync( // NOSONAR S107 — private builder; all params are independent job-spec inputs
        string normalized, string selectorLabelValue, string? model, string? effort,
        string jobName, Guid dispatchId, string? claimedPvc,
        JobTemplate template,
        CancellationToken cancellationToken)
    {
        var ctx = new JobSpecBuilder.BuildContext
        {
            WorkItemId = null,
            AgentSelector = normalized,
            TimeoutSeconds = _options.AgentJobTimeoutSeconds,
            JobName = jobName,
            ClaimedPvc = claimedPvc,
            OrchestratorUrl = _options.OrchestratorUrl,
            AgentApiKeySecretName = _options.AgentApiKeySecretName,
            AgentServiceAccountName = _options.AgentServiceAccountName,
            Namespace = _options.Namespace,
            OpencodeConfigSecretName = IsOpencodeAgent(template.ProviderType)
                ? _options.OpencodeConfigSecretName : null,
            ProjectSecrets = null
        };

        var job = JobSpecBuilder.Build(template, ctx);

        var container = job.Spec.Template.Spec.Containers[0];

        container.Args ??= new List<string>();
        container.Args.Add(AgentDefaults.CliModeChat);

        container.Env ??= new List<V1EnvVar>();
        container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatMode, Value = "true" });
        container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatSessionId, Value = dispatchId.ToString() });
        container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvAgentProviderType, Value = template.ProviderType });

        if (!string.IsNullOrEmpty(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatModel, Value = model });

        if (!string.IsNullOrEmpty(effort) && !effort.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (ValidEffortValues.Contains(effort))
                container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatEffort, Value = effort });
            else
                _logger.Warning("ChatJobDispatcher: invalid effort value rejected: {Effort}", effort);
        }

        job.Metadata.Labels[LabelChatSessionId] = dispatchId.ToString();
        job.Metadata.Labels["caa/chat-selector"] = selectorLabelValue;
        if (claimedPvc is not null)
            job.Metadata.Labels["caa/claimed-pvc"] = claimedPvc;

        job.Spec.BackoffLimit = 0;
        job.Spec.ActiveDeadlineSeconds = _options.AgentJobTimeoutSeconds;
        job.Spec.Template.Spec.TerminationGracePeriodSeconds = _options.ChatTerminationGracePeriodSeconds;

        await _jobClient.CreateJobAsync(job, _options.Namespace, cancellationToken);
    }

    private async Task<string> PollForAgentConnectionAsync( // NOSONAR S107 — private polling helper; params are independent timing/routing inputs
        Guid dispatchId, string jobName, string? claimedPvc, string normalized,
        string selectorLabelValue, DateTimeOffset dispatchStart,
        System.Diagnostics.Activity? activity, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ChatPodConnectTimeoutSeconds));

        try
        {
            while (!timeoutCts.Token.IsCancellationRequested)
            {
                var agents = _registry.GetAgentsByLabel("chat-session-id", dispatchId.ToString());
                var connected = agents.FirstOrDefault(a => a.Status == AgentStatus.Idle);
                if (connected is not null)
                {
                    var elapsed = (DateTimeOffset.UtcNow - dispatchStart).TotalSeconds;
                    _logger.Information(
                        "ChatJobDispatcher: chat agent {AgentId} connected for job {JobName} in {ElapsedSeconds:F1}s",
                        connected.AgentId, jobName, elapsed);

                    // Invariant: for chat pods, agentId == jobName (field ref to metadata.name).
                    // TerminateChatSessionAsync relies on this to look up the watcher by agentId.
                    // Log a warning if the invariant is ever violated (e.g. pod image change).
                    if (!string.Equals(connected.AgentId.Value, jobName, StringComparison.Ordinal))
                    {
                        _logger.Warning(
                            "ChatJobDispatcher: agentId '{AgentId}' != jobName '{JobName}' — invariant violated. " +
                            "TerminateChatSessionAsync will use agentId as jobName; terminate may fail.",
                            connected.AgentId, jobName);
                    }

                    RegisterWatcher(connected.AgentId.Value, jobName, claimedPvc, normalized);

                    var tag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
                    ChatTelemetry.DispatchLatency.Record(elapsed, tag);

                    return connected.AgentId.Value;
                }

                await Task.Delay(500, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal connect timeout
            _logger.Warning(ex,
                "ChatJobDispatcher: chat pod for {AgentSelector} did not connect within {TimeoutSeconds}s — cleaning up {JobName}",
                normalized, _options.ChatPodConnectTimeoutSeconds, jobName);

            var tag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
            ChatTelemetry.PodConnectTimeouts.Add(1, tag);

            activity?.SetStatus(ActivityStatusCode.Error, "Connect timeout");

            await TryCleanupFailedDispatch(jobName, cancellationToken);
            throw new ChatPodTimeoutException(_options.ChatPodConnectTimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            await TryCleanupFailedDispatch(jobName, CancellationToken.None);
            throw;
        }

        // Loop exited because timeout fired between iterations
        _logger.Warning(
            "ChatJobDispatcher: chat pod for {AgentSelector} did not connect within {TimeoutSeconds}s — cleaning up {JobName}",
            normalized, _options.ChatPodConnectTimeoutSeconds, jobName);

        var timeoutTag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
        ChatTelemetry.PodConnectTimeouts.Add(1, timeoutTag);

        activity?.SetStatus(ActivityStatusCode.Error, "Connect timeout");

        await TryCleanupFailedDispatch(jobName, cancellationToken);
        throw new ChatPodTimeoutException(_options.ChatPodConnectTimeoutSeconds);
    }

    // ─── Watcher registration ─────────────────────────────────────────────────

    private void RegisterWatcher(string agentId, string jobName, string? claimedPvc, string selector)
    {
        // Create a linked CTS so this watcher stops when _shutdownCts is cancelled.
        // Stored in the entry so CleanupSession can dispose it — preventing the resource leak
        // that would occur if only the token (not the CTS) were captured.
        var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

        var entry = new WatcherEntry(agentId, jobName, selector, claimedPvc, DateTimeOffset.UtcNow, watcherCts);

        // Key by agentId (not jobName) so TerminateChatSessionAsync can look up by the value
        // returned from DispatchChatPodAsync. In production agentId == jobName (AGENT_ID is set
        // to metadata.name via field ref), but tests may use a custom agentId.
        //
        // IMPORTANT: store in the dictionary BEFORE Task.Run so that if WatchJobUntilTerminalAsync
        // completes synchronously (e.g. immediate 404), CleanupSession's TryRemove runs against
        // an entry that is already present. If we stored after Task.Run, a fast-completing watcher
        // could TryRemove a missing key and then the dictionary write below would re-insert a stale
        // entry, causing HasActiveSession to return true after the session has ended.
        _activeWatchers[agentId] = entry;

        entry.WatcherTask = Task.Run(
            () => WatchJobUntilTerminalAsync(jobName, entry, watcherCts.Token),
            CancellationToken.None);

        var selectorTag = new KeyValuePair<string, object?>(TagAgentSelector, selector.Replace(',', '_'));
        ChatTelemetry.SessionsActive.Add(1, selectorTag);
        if (claimedPvc is not null)
            ChatTelemetry.PvcUtilization.Add(1, new KeyValuePair<string, object?>("pool", "kiro"));

        _logger.Information("ChatJobDispatcher: watcher registered jobName={JobName}", jobName);
    }

    // ─── Circuit-based keepalive ───────────────────────────────────────────────

    /// <summary>
    /// Records a client keepalive heartbeat for the chat session identified by <paramref name="agentId"/>.
    /// Called from <c>POST /api/chat/{agentId}/keepalive</c> whenever the Blazor UI ticks its
    /// keepalive timer. If the session is not found (already terminated or unknown), the call is
    /// a no-op so the endpoint can stay idempotent.
    ///
    /// <para>
    /// Updates both the in-process <see cref="WatcherEntry.LastClientHeartbeatTicks"/> (used when
    /// the keepalive lands on the same replica as the watcher) and a Redis key
    /// <c>chat:heartbeat:{agentId}</c> (authoritative cross-replica source, read by
    /// <see cref="WatchJobUntilTerminalAsync"/> when Redis is configured).
    /// </para>
    /// </summary>
    public void RecordClientHeartbeat(string agentId)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;

        // Update local in-process clock (fast path — same replica)
        if (_activeWatchers.TryGetValue(agentId, out var entry))
            Interlocked.Exchange(ref entry.LastClientHeartbeatTicks, nowTicks);

        // Write to Redis so other replicas' watchers see the heartbeat.
        // TTL = 2× idle timeout — ensures the key is present for the next check cycle even if
        // a heartbeat is delayed by one cycle. Fire-and-forget; failures are non-fatal because
        // the watcher falls back to local ticks when Redis is unavailable.
        if (_redis is not null)
        {
            var ttl = TimeSpan.FromSeconds(_options.ChatIdleTimeoutSeconds * 2);
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            _ = _redis.SetAsync(HeartbeatKey(agentId), nowMs.ToString(), ttl)
                .ContinueWith(t => _logger.Warning(t.Exception,
                    "ChatJobDispatcher: Redis heartbeat write failed for {AgentId}", agentId),
                    TaskContinuationOptions.OnlyOnFaulted);
        }
    }

    // IChatJobDispatcher bridge — routes the interface method to the internal implementation.
    void IChatJobDispatcher.SendClientKeepalive(string agentId) => RecordClientHeartbeat(agentId);

    private static string HeartbeatKey(string agentId) => $"chat:heartbeat:{agentId}";

    /// <summary>
    /// Reads the cross-replica heartbeat timestamp from Redis.
    /// Returns null on Redis failure (watcher falls back to local ticks).
    /// </summary>
    private async Task<DateTimeOffset?> TryGetRedisHeartbeatAsync(string jobName, WatcherEntry entry)
    {
        try
        {
            var raw = await _redis!.GetAsync(HeartbeatKey(entry.AgentId));
            if (raw is not null && long.TryParse(raw, out var ms))
                return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: Redis heartbeat read failed for {JobName} — falling back to local ticks",
                jobName);
        }
        return null;
    }

    // ─── Background watcher ───────────────────────────────────────────────────

    private async Task WatchJobUntilTerminalAsync(
        string jobName, WatcherEntry entry, CancellationToken ct)
    {
        var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
        var idleTimeout = TimeSpan.FromSeconds(_options.ChatIdleTimeoutSeconds);
        // Compute once — constant for the watcher's lifetime.
        // Wake up no later than idleTimeout/3 so we react promptly to window-close.
        var pollInterval = TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, _options.ChatIdleTimeoutSeconds / 3)));

        while (!ct.IsCancellationRequested)
        {
            // ── Circuit-based idle-kill check ──────────────────────────────────
            // If the client has not sent a keepalive within ChatIdleTimeoutSeconds, the
            // chat window is presumed closed (navigate-away, tab crash, browser close).
            // When Redis is configured: read the cross-replica authoritative timestamp so
            // a keepalive that landed on a different replica is visible here. Fall back to
            // the local in-process ticks when Redis is absent (local dev, single replica).
            DateTimeOffset lastHeartbeat;
            if (_redis is not null)
            {
                var raw = await TryGetRedisHeartbeatAsync(jobName, entry);
                lastHeartbeat = raw ?? new DateTimeOffset(
                    Interlocked.Read(ref entry.LastClientHeartbeatTicks), TimeSpan.Zero);
            }
            else
            {
                lastHeartbeat = new DateTimeOffset(
                    Interlocked.Read(ref entry.LastClientHeartbeatTicks), TimeSpan.Zero);
            }

            var idleSince = DateTimeOffset.UtcNow - lastHeartbeat;
            if (idleSince > idleTimeout)
            {
                // Only fire idle-kill if TerminateChatSessionAsync hasn't already been called.
                // Interlocked.CompareExchange: set Terminating from 0 → 1; if it was already 1,
                // someone else is handling termination — back off and let the watcher drain.
                if (Interlocked.CompareExchange(ref entry.Terminating, 1, 0) != 0)
                {
                    _logger.Debug(
                        "ChatJobDispatcher: idle-kill skipped for {JobName} — termination already in progress",
                        jobName);
                    // Let the watcher loop continue; the in-flight termination will clean up.
                    try { await Task.Delay(pollInterval, ct); } catch (OperationCanceledException) { }
                    continue;
                }

                _logger.Warning(
                    "ChatJobDispatcher: chat pod {JobName} idle for {IdleSeconds:F0}s (threshold={Threshold}s) — terminating",
                    jobName, idleSince.TotalSeconds, _options.ChatIdleTimeoutSeconds);
                // Run termination asynchronously but wait for it to complete before exiting.
                await TerminateChatSessionAsync(new AgentId(entry.AgentId), CancellationToken.None);
                return;
            }

            var (job, readError) = await TryReadJobAsync(jobName);

            if (!readError && (job is null || IsTerminal(job)))
            {
                LogJobTermination(job, entry.JobName, entry.ClaimedPvc);
                CleanupSession(entry.AgentId, entry, selectorEncoded, "completed");
                return;
            }

            try
            {
                await Task.Delay(pollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                CleanupSession(entry.AgentId, entry, selectorEncoded, "shutdown");
                return;
            }
        }
        // ct was already cancelled when the while-condition was evaluated
        CleanupSession(entry.AgentId, entry, selectorEncoded, "shutdown");
    }

    private async Task<(V1Job? job, bool readError)> TryReadJobAsync(string jobName)
    {
        try
        {
            var job = await _jobClient.ReadJobAsync(jobName, _options.Namespace, CancellationToken.None);
            return (job, false);
        }
        catch (Exception ex) when (IsNotFound(ex))
        {
            _logger.Information(ex,
                "ChatJobDispatcher: job {JobName} no longer exists in K8s — treating as terminal",
                jobName);
            return (null, false);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: transient ReadJobAsync failure for {JobName} (will retry): {ErrorMessage}",
                jobName, ex.Message);
            return (null, true);
        }
    }

    /// <summary>
    /// Returns true when the exception indicates the K8s job was not found (HTTP 404).
    /// </summary>
    internal static bool IsNotFound(Exception ex)
        => ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("404", StringComparison.Ordinal)
           || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private void LogJobTermination(V1Job? job, string jobName, string? claimedPvc)
    {
        if (job is null)
        {
            _logger.Warning(
                "ChatJobDispatcher: job {JobName} not found (externally deleted) — releasing PVC {Pvc}",
                jobName, claimedPvc ?? "none");
        }
        else
        {
            var isFailed = job.Status?.Conditions?.Any(
                c => c.Type == "Failed" && c.Status == "True") == true;

            if (isFailed)
                _logger.Warning(
                    "ChatJobDispatcher: job {JobName} failed — PVC {Pvc} released",
                    jobName, claimedPvc ?? "none");
            else
                _logger.Information(
                    "ChatJobDispatcher: job {JobName} completed — PVC {Pvc} released",
                    jobName, claimedPvc ?? "none");
        }
    }

    /// <summary>
    /// Atomically cleans up a session. The <c>Interlocked.CompareExchange</c> gate ensures
    /// that only one path (watcher or TerminateChatSessionAsync) runs the cleanup, preventing
    /// double-decrement of metrics when both paths race to see a terminal job.
    /// </summary>
    private void CleanupSession(string agentId, WatcherEntry entry, string selectorEncoded, string outcome)
    {
        // Atomic gate: only the first caller proceeds; the second returns immediately
        if (Interlocked.CompareExchange(ref entry.Cleaned, 1, 0) != 0)
            return;

        _activeWatchers.TryRemove(agentId, out _);

        var selectorTag = new KeyValuePair<string, object?>(TagAgentSelector, selectorEncoded);
        ChatTelemetry.SessionsActive.Add(-1, selectorTag);
        if (entry.ClaimedPvc is not null)
            ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));

        var duration = (DateTimeOffset.UtcNow - entry.StartedAt).TotalSeconds;
        ChatTelemetry.SessionDuration.Record(
            duration,
            selectorTag,
            new KeyValuePair<string, object?>(TagOutcome, outcome));

        try { entry.WatcherCts.Dispose(); }
        catch { /* already disposed — safe to ignore */ }

        // Best-effort Redis cleanup — remove the heartbeat key so it doesn't accumulate
        if (_redis is not null)
            _ = _redis.DeleteAsync(HeartbeatKey(agentId))
                .ContinueWith(t => _logger.Warning(t.Exception,
                    "ChatJobDispatcher: Redis heartbeat key delete failed for {AgentId}", agentId),
                    TaskContinuationOptions.OnlyOnFaulted);
    }

    // ─── IHostedService ───────────────────────────────────────────────────────

    /// <summary>
    /// Emits a startup warning when Redis is absent and <see cref="DispatchServiceOptions.ChatReplicaCount"/>
    /// is greater than 1 — in that configuration, keepalive heartbeats that land on a replica
    /// other than the watcher replica are silently lost, causing chat pods to be idle-killed
    /// despite active browser windows.
    /// Jobs active before this process started drain via <c>ActiveDeadlineSeconds</c>.
    /// Session recovery was removed in Spec 049 alongside leader election.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_redis is null && _options.ChatReplicaCount > 1)
            _logger.Warning(
                "ChatJobDispatcher: Redis is not configured but ChatReplicaCount={Count}. " +
                "Keepalive heartbeats will be invisible to watchers on other replicas — " +
                "chat pods may be idle-killed despite active browser windows. " +
                "Set signalr.redis.connectionString to fix.", _options.ChatReplicaCount);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Idempotency guard: if StopAsync was already called (e.g. ASP.NET Core called both
        // IHostedService.StopAsync and IAsyncDisposable.DisposeAsync → StopAsync in sequence),
        // return immediately so _shutdownCts.CancelAsync() is never invoked twice and cannot
        // race with _shutdownCts.Dispose() in DisposeAsync.
        // TODO: A concurrent second StopAsync caller (not DisposeAsync) returns immediately as a
        // silent no-op rather than waiting for the first call to finish. The comment below about
        // "DisposeAsync guards against this" is only true for DisposeAsync specifically — any other
        // code path that calls StopAsync twice concurrently and then depends on "stopped" state may
        // proceed prematurely. In the current ASP.NET Core host lifecycle this is not exercised
        // (Stop and Dispose are sequential), but it is a latent correctness issue if StopAsync is
        // ever called from outside the hosted service lifecycle.
        // A concurrent second caller returns immediately without waiting for the first to finish;
        // DisposeAsync guards against this by awaiting _stopCompleted before calling Dispose.
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
            return;

        _logger.Information("ChatJobDispatcher: stopping — cancelling {Count} active watcher(s)",
            _activeWatchers.Count);

        // Outer try/finally ensures _stopCompleted is always signalled, even if CancelAsync()
        // throws an unexpected exception (e.g. ObjectDisposedException from an unusual teardown
        // path). Without this guard, any exception thrown before the inner finally block would
        // leave _stopCompleted unset, causing DisposeAsync's `await _stopCompleted.Task` to hang
        // indefinitely and block pod shutdown until the host's ShutdownTimeout kills the process.
        // TODO: The comment above is misleading — the outer try/finally already ensures
        // _stopCompleted.TrySetResult() is called on any exception path, so the "would leave
        // _stopCompleted unset" scenario described cannot actually occur with the current structure.
        // The comment describes the motivation for the try/finally but overstates the risk by
        // implying a gap that the surrounding code already closes. Consider revising to describe
        // the protection that IS provided rather than a failure mode that no longer exists.
        try
        {
            // Signal all watchers to stop.
            await _shutdownCts.CancelAsync();

            // Collect current entries before they drain
            var entries = _activeWatchers.ToArray();

            // Await all watchers with 5s deadline
            try
            {
                await Task.WhenAll(entries.Select(e => e.Value.WatcherTask))
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            }
            catch
            {
                // Timeout or aggregate watcher failure on shutdown — manual cleanup follows
            }

            // Clean up any sessions whose watchers didn't finish in time.
            foreach (var (_, entry) in entries)
            {
                var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
                CleanupSession(entry.AgentId, entry, selectorEncoded, "shutdown");
            }
        }
        finally
        {
            // Signal DisposeAsync that StopAsync has fully completed. This ensures that even if
            // DisposeAsync is called concurrently with an in-progress StopAsync, _shutdownCts.Dispose()
            // is never called while CancelAsync() is still in flight.
            _stopCompleted.TrySetResult();
        }
    }

    // ─── TerminateChatSessionAsync ────────────────────────────────────────────

    /// <summary>
    /// Terminates a chat session by sending CancelChat to the connected agent and, if needed,
    /// force-deleting the K8s Job after the grace period.
    ///
    /// <para>
    /// <b>Invariant:</b> for chat pods, <c>agentId == jobName</c>. The pod's <c>AGENT_ID</c>
    /// environment variable is set via a field ref to <c>metadata.name</c> (the K8s Job name),
    /// so the value reported by the agent at hub registration equals the Job name used as the
    /// key into <see cref="_activeWatchers"/>. If this invariant is ever violated (e.g. a future
    /// pod image change), a warning is logged in <c>PollForAgentConnectionAsync</c> and this
    /// method may fail to locate the correct Job.
    /// </para>
    /// </summary>
    public async Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Terminate");
        activity?.SetTag("agent_id", agentId.Value);

        // agentId is the key into _activeWatchers (set at dispatch time from connected.AgentId.Value).
        // In production agentId == jobName; in tests they may differ — always use entry.JobName for K8s ops.
        if (!_activeWatchers.TryGetValue(agentId.Value, out var entry))
        {
            // No registered watcher — pod may be in the connect-timeout window (dispatched but
            // not yet connected), or was dispatched on another replica. Attempt a best-effort
            // direct delete treating agentId as jobName (correct per production invariant).
            _logger.Information(
                "ChatJobDispatcher: TerminateChatSessionAsync — no watcher for {AgentId}, attempting direct job delete",
                agentId);
            await TryCleanupFailedDispatch(agentId.Value, cancellationToken);
            activity?.SetTag(TagOutcome, "not_found_direct_delete");
            return;
        }

        // Mark as terminating atomically — prevents the idle-kill path in WatchJobUntilTerminalAsync
        // from launching a concurrent TerminateChatSessionAsync when an explicit one is already
        // in flight, which would call CancelChat twice and confuse tests that assert Times.Once.
        Interlocked.Exchange(ref entry.Terminating, 1);

        activity?.SetTag("job_name", entry.JobName);

        // 1. Send CancelChat — best-effort, guarded by CancelSent so concurrent calls are no-ops
        await TrySendCancelChatAsync(agentId.Value, entry);

        // 2. Wait up to grace period for the watcher to confirm terminal
        var gracePeriod = TimeSpan.FromSeconds(_options.ChatTerminationGracePeriodSeconds);
        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceCts.CancelAfter(gracePeriod);

        try
        {
            await entry.WatcherTask.WaitAsync(graceCts.Token);
            activity?.SetTag(TagOutcome, "clean");
        }
        catch (OperationCanceledException)
        {
            // Grace period expired — cancel the watcher so it exits its poll loop promptly (issue #2143).
            // Without this, WatchJobUntilTerminalAsync keeps retrying ReadJobAsync indefinitely when
            // TryReadJobAsync returns readError=true (e.g. K8s API outage), because Dispose() on a
            // CancellationTokenSource does not cancel it; only Cancel() does.
            //
            // Known follow-up (blocking): WatchJobUntilTerminalAsync uses CancellationToken.None for the
            // inner TryReadJobAsync call, so if the K8s API is hanging (e.g. slow TCP timeout ~30s), the
            // watcher will not exit promptly after WatcherCts cancellation — it can block for up to one full
            // TCP-timeout before observing cancellation at the next Task.Delay. Fixing this requires passing
            // WatcherCts.Token into TryReadJobAsync, which is a pre-existing design gap not introduced here.
            // See review finding: DotNetSpecialist WARNING @ line 711.
            try
            {
                await entry.WatcherCts.CancelAsync();
            }
            catch (ObjectDisposedException)
            {
                // WatcherCts was already disposed by CleanupSession — disposed = already cancelled, safe to ignore.
            }
            activity?.SetTag(TagOutcome, "force_delete");
            await ForceDeleteAndCleanupAsync(agentId.Value, entry);
        }
    }

    private async Task TrySendCancelChatAsync(string agentId, WatcherEntry entry)
    {
        // CAS gate: only the first caller sends CancelChat. Concurrent explicit terminate +
        // idle-kill paths both call this method; the gate ensures the agent receives exactly one
        // CancelChat even when both paths race (e.g., user clicks EndChat while idle-kill fires).
        if (Interlocked.CompareExchange(ref entry.CancelSent, 1, 0) != 0)
            return;

        // For chat pods, jobName == agentId in production (AGENT_ID field ref to metadata.name).
        // In tests agentId may differ — always look up by agentId from the registry.
        var agentEntry = _registry.GetByAgentId(agentId);
        if (agentEntry is null)
            return;

        // The DispatchId is stored as the caa/chat-session-id label on the K8s job.
        // Rather than reading K8s, we read it from the registry agent entry's session label.
        var sessionId = agentEntry.Labels
            .FirstOrDefault(l => l.StartsWith("chat-session-id=", StringComparison.Ordinal))
            ?.Substring("chat-session-id=".Length);

        if (string.IsNullOrEmpty(sessionId))
            return;

        try
        {
            await _hubContext.Clients.Client(agentEntry.ConnectionId)
                .CancelChat(sessionId);

            _logger.Information(
                "ChatJobDispatcher: CancelChat sent to agent {AgentId} for job {JobName}",
                agentId, entry.JobName);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: CancelChat to agent {AgentId} failed (will await watcher): {ErrorMessage}",
                agentId, ex.Message);
        }
    }

    private async Task ForceDeleteAndCleanupAsync(string agentId, WatcherEntry entry)
    {
        _logger.Warning(
            "ChatJobDispatcher: grace period expired for {JobName} — force deleting job", entry.JobName);

        try
        {
            await _jobClient.DeleteJobAsync(entry.JobName, _options.Namespace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: force delete failed for {JobName}: {ErrorMessage}",
                entry.JobName, ex.Message);
        }

        // Eagerly remove from registry before CleanupSession so the chat agent is not visible
        // as Disconnected in the UI after the pod exits. Deregister is idempotent — if
        // OnDisconnectedAsync fires first, the second call is a safe no-op.
        _registry.Deregister(new AgentId(agentId));

        var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
        CleanupSession(agentId, entry, selectorEncoded, "force_deleted");
        ChatTelemetry.PodForceTerminations.Add(1,
            new KeyValuePair<string, object?>(TagAgentSelector, selectorEncoded));
    }

    // ─── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        // StopAsync sets _stopCompleted when it finishes. Awaiting it here ensures that
        // _shutdownCts.Dispose() is never called while a concurrent in-progress StopAsync
        // is still awaiting CancelAsync() — eliminating a potential ObjectDisposedException
        // in that race window. In the sequential ASP.NET Core lifecycle (Stop then Dispose)
        // this completes immediately because StopAsync already finished.
        await StopAsync(CancellationToken.None);
        // Awaits the first in-flight StopAsync when the idempotency guard returned early above;
        // no-op in the sequential ASP.NET Core lifecycle where StopAsync already set _stopCompleted.
        await _stopCompleted.Task;
        _shutdownCts.Dispose();
    }

    // ─── Private helpers ──────────────────────────────────────────────────────

    private async Task TryCleanupFailedDispatch(string jobName, CancellationToken ct)
    {
        try { await _jobClient.DeleteJobAsync(jobName, _options.Namespace, ct); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ChatJobDispatcher: cleanup delete failed for {JobName}", jobName);
        }
    }

    internal static bool IsTerminal(V1Job job)
        => job.Status?.Conditions?.Any(c =>
               (c.Type == "Complete" || c.Type == "Failed") && c.Status == "True") == true;

    internal static bool IsKiroAgent(string providerType)
        => string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpencodeAgent(string providerType)
        => string.Equals(providerType, "opencode", StringComparison.OrdinalIgnoreCase);

    // ─── Test helpers (internal) ──────────────────────────────────────────────

    /// <summary>
    /// Returns true if there is an active watcher for the given agentId (== jobName for chat pods).
    /// </summary>
    internal bool HasActiveSession(string agentId)
        => _activeWatchers.ContainsKey(agentId);

    /// <summary>
    /// Waits for the watcher task for the given agentId to complete within the timeout.
    /// Returns true if the watcher finished, false if it timed out.
    /// </summary>
    internal async Task<bool> WaitForWatcherAsync(string agentId, TimeSpan timeout)
    {
        if (!_activeWatchers.TryGetValue(agentId, out var entry))
            return true; // Already cleaned up

        try
        {
            await entry.WatcherTask.WaitAsync(timeout, CancellationToken.None);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static readonly HashSet<string> ValidEffortValues =
        new(["high", "medium", "low"], StringComparer.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9._\-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex K8sLabelValuePattern();
}
