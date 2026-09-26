using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using CodingAgent.Pipeline;
using CodingAgent.AgentGateway;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Telemetry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

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
    // Optional — null when Redis is not configured.
    private readonly IChatHeartbeatTracker? _heartbeatTracker;
    // Owns the watcher loop logic (extracted collaborator).
    private readonly IChatSessionWatcher _sessionWatcher;

    private const string TagAgentSelector = "agent_selector";
    private const string LabelChatSessionId = "caa/chat-session-id";
    private const string TagOutcome = "outcome";

    // ── Minimal session tracking ──────────────────────────────────────────────
    // Keyed by jobName (== agentId for chat pods — see invariant note on TerminateChatSessionAsync).
    private readonly ConcurrentDictionary<string, WatcherEntry> _activeWatchers = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    /// <summary>
    /// Groups the co-travelling identity and selector fields that flow from
    /// <see cref="PollForAgentConnectionAsync"/> into <see cref="RegisterWatcher"/> and
    /// ultimately into <see cref="WatcherEntry"/>. Using a record prevents silent positional
    /// transposition of the four same-typed strings (agentId, jobName, normalizedSelector,
    /// claimedPvc) whose ordering differed between <see cref="RegisterWatcher"/> and the
    /// <see cref="WatcherEntry"/> constructor.
    /// </summary>
    internal sealed record WatcherIdentity(
        AgentId AgentId,
        string JobName,
        string NormalizedSelector,
        string? ClaimedPvc);

    /// <summary>
    /// Tracks per-session watcher state. Must be <c>internal</c> so <see cref="IChatSessionWatcher"/>
    /// can reference it without requiring a public type.
    /// </summary>
    internal sealed class WatcherEntry
    {
        public Task WatcherTask = Task.CompletedTask; // assigned after construction; see RegisterWatcher
        public readonly AgentId AgentId;  // dict key (.Value); == jobName in production, may differ in tests
        public readonly string JobName;
        public readonly string NormalizedSelector;
        public readonly string? ClaimedPvc;
        public readonly DateTimeOffset StartedAt;
        public readonly CancellationTokenSource WatcherCts; // disposed in CleanupSession
        public int Cleaned; // 0 = not yet cleaned; 1 = cleanup done. Used with Interlocked.

        // Circuit-based lifecycle: tracks last client keepalive. Initialised to StartedAt so the
        // idle clock starts from dispatch, not from an arbitrary epoch.
        public long LastClientHeartbeatTicks; // written/read with Interlocked for thread safety

        // Guard: 0 = not yet terminating; 1 = termination in progress or complete.
        public int Terminating;

        // Guard: 0 = CancelChat not yet sent; 1 = already sent.
        public int CancelSent;

        public WatcherEntry(WatcherIdentity identity, DateTimeOffset startedAt, CancellationTokenSource watcherCts)
        {
            AgentId = identity.AgentId;
            JobName = identity.JobName;
            NormalizedSelector = identity.NormalizedSelector;
            ClaimedPvc = identity.ClaimedPvc;
            StartedAt = startedAt;
            WatcherCts = watcherCts;
            LastClientHeartbeatTicks = startedAt.UtcTicks;
        }
    }

    /// <summary>
    /// Public constructor used by tests and call sites that don't inject the extracted collaborators.
    /// When <paramref name="redis"/> is non-null, a <see cref="ChatHeartbeatTracker"/> is created
    /// internally; when null, heartbeat tracking is local-only.
    /// </summary>
    public ChatJobDispatcher(
        IKubernetesJobClient jobClient,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        JobTemplateStore templateStore,
        IAgentRegistryService registry,
        DispatchServiceOptions options,
        ILogger logger,
        CodingAgent.Orchestration.Redis.IRedisStore? redis = null)
        : this(
            jobClient, hubContext, templateStore, registry, options, logger,
            redis is not null ? new ChatHeartbeatTracker(redis, options, logger) : (IChatHeartbeatTracker?)null,
            null)
    {
    }

    /// <summary>
    /// Internal constructor used by the DI lambda in <c>ApiServiceCollectionExtensions</c>
    /// to inject pre-constructed collaborators. <paramref name="heartbeatTracker"/> and
    /// <paramref name="sessionWatcher"/> are <c>internal</c> types and cannot appear on a
    /// <c>public</c> constructor.
    /// </summary>
    internal ChatJobDispatcher(
        IKubernetesJobClient jobClient,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        JobTemplateStore templateStore,
        IAgentRegistryService registry,
        DispatchServiceOptions options,
        ILogger logger,
        IChatHeartbeatTracker? heartbeatTracker,
        IChatSessionWatcher? sessionWatcher)
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
        _heartbeatTracker = heartbeatTracker;
        _sessionWatcher = sessionWatcher ?? new ChatSessionWatcher(jobClient, heartbeatTracker, options, logger);
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
            TimeoutSeconds = _options.ChatJobMaxDurationSeconds,
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
            if (KiroCliSettingsWriter.ValidEffortValues.Contains(effort))
                container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatEffort, Value = effort });
            else
                _logger.Warning("ChatJobDispatcher: invalid effort value rejected: {Effort}", LogSanitizer.SanitizeForLog(effort));
        }

        job.Metadata.Labels[LabelChatSessionId] = dispatchId.ToString();
        job.Metadata.Labels["caa/chat-selector"] = selectorLabelValue;
        if (claimedPvc is not null)
            job.Metadata.Labels["caa/claimed-pvc"] = claimedPvc;

        job.Spec.BackoffLimit = 0;
        job.Spec.ActiveDeadlineSeconds = _options.ChatJobMaxDurationSeconds;
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

                    if (!string.Equals(connected.AgentId.Value, jobName, StringComparison.Ordinal))
                    {
                        _logger.Warning(
                            "ChatJobDispatcher: agentId '{AgentId}' != jobName '{JobName}' — invariant violated. " +
                            "TerminateChatSessionAsync will use agentId as jobName; terminate may fail.",
                            connected.AgentId, jobName);
                    }

                    RegisterWatcher(new WatcherIdentity(connected.AgentId, jobName, normalized, claimedPvc));

                    var tag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
                    ChatTelemetry.DispatchLatency.Record(elapsed, tag);

                    return connected.AgentId.Value;
                }

                await Task.Delay(500, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Internal connect timeout — delegate to shared helper
            await HandleConnectTimeoutAsync(normalized, selectorLabelValue, jobName, activity, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            await TryCleanupFailedDispatch(jobName, CancellationToken.None);
            throw;
        }

        // Loop exited because timeout fired between iterations — delegate to shared helper
        await HandleConnectTimeoutAsync(normalized, selectorLabelValue, jobName, activity, cancellationToken);

        // HandleConnectTimeoutAsync always throws, so this is unreachable.
        // Required to satisfy the compiler's control-flow analysis.
        throw new InvalidOperationException("HandleConnectTimeoutAsync must have thrown.");
    }

    /// <summary>
    /// Shared connect-timeout cleanup sequence. Called from both the
    /// <c>OperationCanceledException catch when !cancellationToken.IsCancellationRequested</c>
    /// arm and the post-loop path of <see cref="PollForAgentConnectionAsync"/>. Always throws
    /// <see cref="ChatPodTimeoutException"/>.
    /// </summary>
    private async Task HandleConnectTimeoutAsync(
        string normalized,
        string selectorLabelValue,
        string jobName,
        System.Diagnostics.Activity? activity,
        CancellationToken cancellationToken)
    {
        _logger.Warning(
            "ChatJobDispatcher: chat pod for {AgentSelector} did not connect within {TimeoutSeconds}s — cleaning up {JobName}",
            normalized, _options.ChatPodConnectTimeoutSeconds, jobName);

        var tag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
        ChatTelemetry.PodConnectTimeouts.Add(1, tag);

        activity?.SetStatus(ActivityStatusCode.Error, "Connect timeout");

        await TryCleanupFailedDispatch(jobName, cancellationToken);
        throw new ChatPodTimeoutException(_options.ChatPodConnectTimeoutSeconds);
    }

    // ─── Watcher registration ─────────────────────────────────────────────────

    private void RegisterWatcher(WatcherIdentity identity)
    {
        var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);

        var entry = new WatcherEntry(identity, DateTimeOffset.UtcNow, watcherCts);

        // IMPORTANT: store in the dictionary BEFORE Task.Run so that if WatchJobUntilTerminalAsync
        // completes synchronously (e.g. immediate 404), CleanupSession's TryRemove runs against
        // an entry that is already present.
        _activeWatchers[identity.AgentId.Value] = entry;

        entry.WatcherTask = Task.Run(
            () => _sessionWatcher.WatchJobUntilTerminalAsync(
                identity.JobName,
                entry,
                (id, ct) => TerminateChatSessionAsync(id, ct),
                (agentId, e, selectorEncoded, outcome) => CleanupSession(agentId, e, selectorEncoded, outcome),
                watcherCts.Token),
            CancellationToken.None);

        var selectorTag = new KeyValuePair<string, object?>(TagAgentSelector, identity.NormalizedSelector.Replace(',', '_'));
        ChatTelemetry.SessionsActive.Add(1, selectorTag);
        if (identity.ClaimedPvc is not null)
            ChatTelemetry.PvcUtilization.Add(1, new KeyValuePair<string, object?>("pool", "kiro"));

        _logger.Information("ChatJobDispatcher: watcher registered jobName={JobName}", identity.JobName);
    }

    // ─── Circuit-based keepalive ───────────────────────────────────────────────

    /// <summary>
    /// Records a client keepalive heartbeat for the chat session identified by <paramref name="agentId"/>.
    /// Updates the in-process local ticks directly (fast path for same-replica checks) and delegates
    /// the Redis write to <see cref="IChatHeartbeatTracker"/> (cross-replica path).
    /// </summary>
    public void RecordClientHeartbeat(AgentId agentId)
    {
        var nowTicks = DateTimeOffset.UtcNow.UtcTicks;

        // Update local in-process clock (fast path — same replica).
        // Interlocked.Exchange stays here because it operates directly on WatcherEntry.LastClientHeartbeatTicks,
        // an internal field that the tracker has no access to.
        if (_activeWatchers.TryGetValue(agentId.Value, out var entry))
            Interlocked.Exchange(ref entry.LastClientHeartbeatTicks, nowTicks);

        // Write to Redis so other replicas' watchers see the heartbeat.
        // TTL = 2× idle timeout. Fire-and-forget via the tracker.
        if (_heartbeatTracker is not null)
            _ = _heartbeatTracker.WriteRedisHeartbeatAsync(agentId);
    }

    // IChatJobDispatcher bridge — routes the interface method to the internal implementation.
    // TODO [WARNING]: IChatJobDispatcher.SendClientKeepalive(string) was not migrated to AgentId as
    // part of issue #2996. The implicit (AgentId)(string) conversion here works correctly for valid
    // non-null/non-empty strings, but a null or empty agentId arriving at this bridge will throw
    // ArgumentException from inside the implicit operator rather than an explicit guard at the public
    // boundary. The regex guard in ChatEndpoints prevents null/empty from the HTTP path in practice,
    // but this is an unintended behavior difference from the removed ArgumentNullException.ThrowIfNull
    // guards. Migrate IChatJobDispatcher.SendClientKeepalive to AgentId to eliminate the implicit
    // conversion and make the guard explicit at the interface boundary.
    // See review findings: DotNetSpecialist WARNING @ ChatJobDispatcher.cs:443, Correctness WARNING @ ApiChatJobDispatcher.cs:57.
    void IChatJobDispatcher.SendClientKeepalive(string agentId) => RecordClientHeartbeat(agentId);

    // ─── CleanupSession ───────────────────────────────────────────────────────

    /// <summary>
    /// Atomically cleans up a session. The <c>Interlocked.CompareExchange</c> gate ensures
    /// that only one path (watcher or TerminateChatSessionAsync) runs the cleanup, preventing
    /// double-decrement of metrics when both paths race to see a terminal job.
    /// Stays on <see cref="ChatJobDispatcher"/> because it owns <see cref="_activeWatchers"/>.
    /// </summary>
    internal void CleanupSession(AgentId agentId, WatcherEntry entry, string selectorEncoded, string outcome)
    {
        if (Interlocked.CompareExchange(ref entry.Cleaned, 1, 0) != 0)
            return;

        _activeWatchers.TryRemove(agentId.Value, out _);

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

        // Best-effort Redis cleanup via tracker
        if (_heartbeatTracker is not null)
            _ = _heartbeatTracker.DeleteRedisHeartbeatAsync(agentId);
    }

    // ─── IHostedService ───────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_heartbeatTracker is null && _options.ChatReplicaCount > 1)
            _logger.Warning(
                "ChatJobDispatcher: Redis is not configured but ChatReplicaCount={Count}. " +
                "Keepalive heartbeats will be invisible to watchers on other replicas — " +
                "chat pods may be idle-killed despite active browser windows. " +
                "Set signalr.redis.connectionString to fix.", _options.ChatReplicaCount);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // TODO [WARNING]: StopAsync is no longer idempotent after removal of the _stopCompleted guard.
        // See pre-existing issue documented in earlier TODOs. Do not fix in this refactor.
        _logger.Information("ChatJobDispatcher: stopping — cancelling {Count} active watcher(s)",
            _activeWatchers.Count);

        await _shutdownCts.CancelAsync();

        var entries = _activeWatchers.ToArray();

        try
        {
            await Task.WhenAll(entries.Select(e => e.Value.WatcherTask))
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // Timeout or aggregate watcher failure on shutdown — manual cleanup follows
        }

        foreach (var (_, entry) in entries)
        {
            var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
            CleanupSession(entry.AgentId, entry, selectorEncoded, "shutdown");
        }
    }

    // ─── TerminateChatSessionAsync ────────────────────────────────────────────

    public async Task TerminateChatSessionAsync(AgentId agentId, CancellationToken cancellationToken)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Terminate");
        activity?.SetTag("agent_id", agentId.Value);

        if (!_activeWatchers.TryGetValue(agentId.Value, out var entry))
        {
            _logger.Information(
                "ChatJobDispatcher: TerminateChatSessionAsync — no watcher for {AgentId}, attempting direct job delete",
                agentId);
            await TryCleanupFailedDispatch(agentId.Value, cancellationToken);
            activity?.SetTag(TagOutcome, "not_found_direct_delete");
            return;
        }

        Interlocked.Exchange(ref entry.Terminating, 1);

        activity?.SetTag("job_name", entry.JobName);

        await TrySendCancelChatAsync(agentId, entry);

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
            // TODO [WARNING]: entry.WatcherCts.CancelAsync() can throw ObjectDisposedException if
            // CleanupSession (which disposes WatcherCts) has already run on the watcher thread before
            // this grace-period catch fires. The race window is widened by the new catch(Exception) fault
            // path in ChatSessionWatcher which calls CleanupSession("faulted") earlier than the prior code.
            // Fix: wrap CancelAsync() in try/catch(ObjectDisposedException).
            // See review finding: DotNetSpecialist WARNING @ ChatJobDispatcher.cs:551.
            try { await entry.WatcherCts.CancelAsync(); }
            catch (ObjectDisposedException) { /* WatcherCts already disposed by CleanupSession on the watcher thread — safe to ignore */ }
            activity?.SetTag(TagOutcome, "force_delete");
            await ForceDeleteAndCleanupAsync(agentId, entry);
        }
    }

    private async Task TrySendCancelChatAsync(AgentId agentId, WatcherEntry entry)
    {
        if (Interlocked.CompareExchange(ref entry.CancelSent, 1, 0) != 0)
            return;

        var agentEntry = await _registry.GetByAgentIdAsync(agentId, CancellationToken.None);
        if (agentEntry is null)
            return;

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

    private async Task ForceDeleteAndCleanupAsync(AgentId agentId, WatcherEntry entry)
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

        _registry.Deregister(agentId);

        var selectorEncoded = entry.NormalizedSelector.Replace(',', '_');
        CleanupSession(agentId, entry, selectorEncoded, "force_deleted");
        ChatTelemetry.PodForceTerminations.Add(1,
            new KeyValuePair<string, object?>(TagAgentSelector, selectorEncoded));
    }

    // ─── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
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

    /// <summary>
    /// Returns true when the exception indicates the K8s job was not found (HTTP 404).
    /// </summary>
    internal static bool IsNotFound(Exception ex)
        => ex.Message.Contains("NotFound", StringComparison.OrdinalIgnoreCase)
           || ex.Message.Contains("404", StringComparison.Ordinal)
           || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTerminal(V1Job job)
        => job.Status?.Conditions?.Any(c =>
               (c.Type == "Complete" || c.Type == "Failed") && c.Status == "True") == true;

    internal static bool IsKiroAgent(string providerType)
        => string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase);

    internal static bool IsOpencodeAgent(string providerType)
        => string.Equals(providerType, "opencode", StringComparison.OrdinalIgnoreCase);

    // ─── Test helpers (internal) ──────────────────────────────────────────────

    internal bool HasActiveSession(string agentId)
        => _activeWatchers.ContainsKey(agentId);

    internal async Task<bool> WaitForWatcherAsync(string agentId, TimeSpan timeout)
    {
        if (!_activeWatchers.TryGetValue(agentId, out var entry))
            return true;

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

    internal Task? TryGetWatcherTask(string agentId)
        => _activeWatchers.TryGetValue(agentId, out var entry) ? entry.WatcherTask : null;

    internal (AgentId AgentId, string JobName, string NormalizedSelector, string? ClaimedPvc)?
        TryGetWatcherFields(string agentId)
    {
        if (!_activeWatchers.TryGetValue(agentId, out var entry))
            return null;
        return (entry.AgentId, entry.JobName, entry.NormalizedSelector, entry.ClaimedPvc);
    }

    /// <summary>
    /// Exposes the heartbeat tracker for test inspection.
    /// </summary>
    internal IChatHeartbeatTracker? HeartbeatTrackerForTest => _heartbeatTracker;

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9._\-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex K8sLabelValuePattern();
}
