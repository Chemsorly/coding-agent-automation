using System.Collections.Concurrent;
using System.Diagnostics;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;
using k8s.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Dispatches on-demand ephemeral chat pods as K8s Jobs, polls for agent connection,
/// maintains per-session background watchers for PVC release on job terminal, and
/// handles terminate/cleanup on navigate-away.
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
    private readonly AgentRegistryService _registry;
    private readonly DispatchServiceOptions _options;
    private readonly ILeaderElectionService _leaderElection;
    private readonly ILogger _logger;

    private const string TagAgentSelector = "agent_selector";
    private const string LabelChatSessionId = "caa/chat-session-id";
    private const string TagOutcome = "outcome";

    private readonly ConcurrentDictionary<string, ChatSession> _sessions = new();
    private readonly ConcurrentDictionary<string, string> _agentIdToJobName = new();

    private sealed record ChatSession
    {
        public required string JobName { get; init; }
        public required string AgentId { get; init; }
        public required string? ClaimedPvc { get; init; }
        public required string NormalizedSelector { get; init; }
        public required Guid DispatchId { get; init; }
        public required Task WatcherTask { get; init; }
        public required CancellationTokenSource WatcherCts { get; init; }
        public required DateTimeOffset ConnectedAt { get; init; }
    }

    public ChatJobDispatcher(
        IKubernetesJobClient jobClient,
        IHubContext<AgentHub, IAgentHubClient> hubContext,
        JobTemplateStore templateStore,
        AgentRegistryService registry,
        DispatchServiceOptions options,
        ILeaderElectionService leaderElection,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(jobClient);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(templateStore);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(leaderElection);
        ArgumentNullException.ThrowIfNull(logger);
        _jobClient = jobClient;
        _hubContext = hubContext;
        _templateStore = templateStore;
        _registry = registry;
        _options = options;
        _leaderElection = leaderElection;
        _logger = logger;
    }

    // ─── DispatchChatPodAsync ──────────────────────────────────────────────────

    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(agentSelector);
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Dispatch");

        var (normalized, selectorLabelValue) = ValidateAndNormalizeSelector(agentSelector);
        activity?.SetTag(TagAgentSelector, normalized);

        // Query all active chat jobs — used for both double-dispatch guard and PVC availability.
        // Using a single broad query (label key presence, no value filter) is replica-safe:
        // each orchestrator replica sees the full cluster state rather than its own in-memory sessions.
        var allChatJobs = await _jobClient.ListJobsAsync(
            _options.Namespace, LabelChatSessionId, cancellationToken);

        var activeChatJobs = allChatJobs.Items?
            .Where(j => !IsTerminal(j))
            .ToList() ?? [];

        CheckForExistingJob(activeChatJobs, selectorLabelValue, normalized);

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

    private (string normalized, string selectorLabelValue) ValidateAndNormalizeSelector(string agentSelector)
    {
        if (!_leaderElection.IsLeader)
            throw new InvalidOperationException(
                "This orchestrator replica is not the leader and cannot dispatch chat pods.");

        var normalized = JobTemplateStore.NormalizeLabels(agentSelector);
        var selectorLabelValue = normalized.Replace(',', '_');

        if (!K8sLabelValuePattern().IsMatch(selectorLabelValue))
            throw new ArgumentException(
                $"Agent selector '{agentSelector}' produces an invalid k8s label value '{selectorLabelValue}'. " +
                "Label values must match [a-zA-Z0-9._-] and be ≤63 characters.");

        return (normalized, selectorLabelValue);
    }

    private void CheckForExistingJob(List<V1Job> activeChatJobs, string selectorLabelValue, string normalized)
    {
        var existingForSelector = activeChatJobs.FirstOrDefault(j =>
        {
            var labels = j.Metadata?.Labels;
            return labels is not null && labels.TryGetValue("caa/chat-selector", out var v) && v == selectorLabelValue;
        });
        if (existingForSelector is not null)
        {
            var existingName = existingForSelector.Metadata?.Name ?? "unknown";
            _logger.Information(
                "ChatJobDispatcher: double-dispatch blocked for selector {AgentSelector} — existing job {JobName}",
                normalized, existingName);
            throw new ChatAlreadyActiveException(existingName);
        }
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

    private async Task BuildAndSubmitChatJobAsync(
        string normalized, string selectorLabelValue, string? model, string? effort,
        string jobName, Guid dispatchId, string? claimedPvc,
        JobTemplate template,
        CancellationToken cancellationToken)
    {
        var ctx = new JobSpecBuilder.BuildContext
        {
            WorkItemId = null,
            AgentSelector = normalized,
            TimeoutSeconds = _options.ChatSessionMaxDurationSeconds,
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
        container.Env ??= new List<V1EnvVar>();
        container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatMode, Value = "true" });
        container.Env.Add(new V1EnvVar { Name = AgentDefaults.EnvChatSessionId, Value = dispatchId.ToString() });

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
        job.Spec.ActiveDeadlineSeconds = _options.ChatSessionMaxDurationSeconds;
        job.Spec.Template.Spec.TerminationGracePeriodSeconds = _options.ChatTerminationGracePeriodSeconds;

        await _jobClient.CreateJobAsync(job, _options.Namespace, cancellationToken);
    }

    private async Task<string> PollForAgentConnectionAsync(
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

                    RegisterSession(dispatchId, jobName, connected.AgentId, claimedPvc, normalized);

                    var tag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
                    ChatTelemetry.DispatchLatency.Record(elapsed, tag);

                    return connected.AgentId;
                }

                await Task.Delay(2000, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our internal timeout — not outer cancellation
            _logger.Warning(
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
            // Outer cancellation (app shutdown)
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            await TryCleanupFailedDispatch(jobName, CancellationToken.None);
            throw;
        }

        // Loop exited because timeoutCts was already cancelled when the while-condition was evaluated
        // (i.e., the timeout fired between iterations rather than inside Task.Delay).
        // Cleanup is required here too — same as the OperationCanceledException catch path.
        _logger.Warning(
            "ChatJobDispatcher: chat pod for {AgentSelector} did not connect within {TimeoutSeconds}s — cleaning up {JobName}",
            normalized, _options.ChatPodConnectTimeoutSeconds, jobName);

        var timeoutTag = new KeyValuePair<string, object?>(TagAgentSelector, selectorLabelValue);
        ChatTelemetry.PodConnectTimeouts.Add(1, timeoutTag);

        activity?.SetStatus(ActivityStatusCode.Error, "Connect timeout");

        await TryCleanupFailedDispatch(jobName, cancellationToken);
        throw new ChatPodTimeoutException(_options.ChatPodConnectTimeoutSeconds);
    }

    // ─── RegisterSession ──────────────────────────────────────────────────────

    private void RegisterSession(
        Guid dispatchId, string jobName, string agentId, string? claimedPvc, string selector)
    {
        var cts = new CancellationTokenSource();
        var session = new ChatSession
        {
            JobName = jobName,
            AgentId = agentId,
            ClaimedPvc = claimedPvc,
            NormalizedSelector = selector,
            DispatchId = dispatchId,
            WatcherCts = cts,
            ConnectedAt = DateTimeOffset.UtcNow,
            WatcherTask = Task.Run(() => WatchJobUntilTerminalAsync(jobName, claimedPvc, selector, cts.Token), cts.Token)
        };
        _sessions[jobName] = session;

        // Secondary index: only when agentId is known (non-empty) — restart recovery passes ""
        if (!string.IsNullOrEmpty(agentId))
            _agentIdToJobName[agentId] = jobName;

        // Metrics
        var selectorTag = new KeyValuePair<string, object?>(TagAgentSelector, selector.Replace(',', '_'));
        ChatTelemetry.SessionsActive.Add(1, selectorTag);
        if (claimedPvc is not null)
            ChatTelemetry.PvcUtilization.Add(1, new KeyValuePair<string, object?>("pool", "kiro"));

        _logger.Information(
            "ChatJobDispatcher: session registered jobName={JobName} agentId={AgentId} model=n/a effort=n/a",
            jobName, agentId);
    }

    // ─── WatchJobUntilTerminalAsync ───────────────────────────────────────────

    private async Task WatchJobUntilTerminalAsync(
        string jobName, string? claimedPvc, string selector, CancellationToken ct)
    {
        var selectorEncoded = selector.Replace(',', '_');

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), ct);
            }
            catch (OperationCanceledException)
            {
                // Intentional: StopAsync cancelled the watcher CTS during graceful shutdown; caller handles PVC release.
                return;
            }

            var (job, readError) = await TryReadJobAsync(jobName);
            if (readError)
                continue; // Transient failure — retry next iteration

            if (job is null || IsTerminal(job))
            {
                LogJobTermination(job, jobName, claimedPvc);

                // Release PVC + update metrics — gated inside TryRemove to prevent
                // double-decrement if TerminateChatSessionAsync already did cleanup.
                var selectorTag = new KeyValuePair<string, object?>(TagAgentSelector, selectorEncoded);

                if (_sessions.TryRemove(jobName, out var removed))
                {
                    CleanupTerminatedSession(removed, selectorTag);
                }

                return;
            }
        }
        // Cancellation path: caller (StopAsync) releases PVC
    }

    private async Task<(V1Job? job, bool readError)> TryReadJobAsync(string jobName)
    {
        try
        {
            var job = await _jobClient.ReadJobAsync(jobName, _options.Namespace, CancellationToken.None);
            return (job, false);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "ChatJobDispatcher: transient ReadJobAsync failure for {JobName} (will retry): {ErrorMessage}",
                jobName, ex.Message);
            return (null, true);
        }
    }

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
                    "ChatJobDispatcher: job {JobName} completed — PVC {Pvc} released, duration=n/a",
                    jobName, claimedPvc ?? "none");
        }
    }

    private void CleanupTerminatedSession(ChatSession removed, KeyValuePair<string, object?> selectorTag)
    {
        if (removed.ClaimedPvc is not null)
            ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));

        ChatTelemetry.SessionsActive.Add(-1, selectorTag);

        var duration = (DateTimeOffset.UtcNow - removed.ConnectedAt).TotalSeconds;
        ChatTelemetry.SessionDuration.Record(
            duration,
            selectorTag,
            new KeyValuePair<string, object?>(TagOutcome, "completed"));

        if (!string.IsNullOrEmpty(removed.AgentId))
            _agentIdToJobName.TryRemove(removed.AgentId, out _);

        try { removed.WatcherCts.Dispose(); } catch { /* Intentional: CTS may already be disposed if watcher exited concurrently. */ }
    }

    // ─── IHostedService ───────────────────────────────────────────────────────

    /// <summary>
    /// Restores in-memory watcher state for any active chat jobs after orchestrator restart.
    /// Waits for leadership before recovering sessions — non-leader replicas must not spin up
    /// background watchers for jobs they don't own.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Launch session recovery in the background so StartAsync returns immediately.
        // The recovery loop waits for leadership before restoring watcher state,
        // preventing non-leader replicas from spinning up watchers they don't own.
        _ = Task.Run(() => RecoverSessionsAsync(cancellationToken), cancellationToken);
        return Task.CompletedTask;
    }

    private async Task RecoverSessionsAsync(CancellationToken ct)
    {
        // Wait for leadership before recovering sessions.
        // Non-leader replicas must not spin up watchers for jobs they don't own.
        while (!ct.IsCancellationRequested && !_leaderElection.IsLeader)
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

        if (ct.IsCancellationRequested)
            return;

        try
        {
            var jobs = await _jobClient.ListJobsAsync(_options.Namespace, LabelChatSessionId, ct);
            foreach (var job in (jobs.Items ?? []).Where(j => !IsTerminal(j)))
            {
                try
                {
                    var labels = job.Metadata?.Labels ?? new Dictionary<string, string>();
                    labels.TryGetValue(LabelChatSessionId, out var sessionIdStr);
                    if (!Guid.TryParse(sessionIdStr, out var sessionId))
                    {
                        _logger.Warning(
                            "ChatJobDispatcher: chat job {Name} has missing or invalid caa/chat-session-id label — skipping",
                            job.Metadata?.Name);
                        continue;
                    }
                    labels.TryGetValue("caa/claimed-pvc", out var pvcLabel);
                    labels.TryGetValue("caa/chat-selector", out var selectorLabel);

                    RegisterSession(sessionId, job.Metadata!.Name, agentId: "", pvcLabel, selectorLabel ?? "");

                    _logger.Information(
                        "ChatJobDispatcher: restored session {JobName} (selector={Selector}) from k8s on startup",
                        job.Metadata.Name, selectorLabel);
                }
                catch (Exception ex)
                {
                    _logger.Warning(ex,
                        "ChatJobDispatcher: skipping malformed chat job {Name} during restart recovery",
                        job.Metadata?.Name);
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: failed to restore sessions from k8s on startup (non-fatal): {ErrorMessage}",
                ex.Message);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var sessions = _sessions.Values.ToList();
        _logger.Information("ChatJobDispatcher: stopping — releasing {Count} active session(s)", sessions.Count);

        foreach (var session in sessions)
            await session.WatcherCts.CancelAsync();

        // Await watchers with 5s timeout — swallow any exception/timeout
        try
        {
            await Task.WhenAll(sessions.Select(s => s.WatcherTask))
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // Intentional: timeout (TimeoutException) or aggregate watcher failure on shutdown; manual cleanup follows.
        }

        // Clean up sessions whose watchers didn't complete
        foreach (var session in sessions)
        {
            if (_sessions.TryRemove(session.JobName, out var removed))
            {
                if (!string.IsNullOrEmpty(removed.AgentId))
                    _agentIdToJobName.TryRemove(removed.AgentId, out _);

                // Decrement metrics for sessions the watcher didn't clean up
                var selectorTag = new KeyValuePair<string, object?>(
                    TagAgentSelector, removed.NormalizedSelector.Replace(',', '_'));
                ChatTelemetry.SessionsActive.Add(-1, selectorTag);
                if (removed.ClaimedPvc is not null)
                    ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));
            }

            try { session.WatcherCts.Dispose(); } catch { /* already disposed */ }
        }
    }

    // ─── TerminateChatSessionAsync ────────────────────────────────────────────

    public async Task TerminateChatSessionAsync(string agentId, CancellationToken cancellationToken)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Terminate");
        activity?.SetTag("agent_id", agentId);

        if (!_leaderElection.IsLeader)
        {
            _logger.Warning(
                "ChatJobDispatcher: TerminateChatSessionAsync called on non-leader replica for agent {AgentId} — no-op",
                agentId);
            return;
        }

        if (!_agentIdToJobName.TryGetValue(agentId, out var jobName))
        {
            activity?.SetTag(TagOutcome, "not_found");
            return;
        }

        if (!_sessions.TryGetValue(jobName, out var session))
        {
            activity?.SetTag(TagOutcome, "not_found");
            return;
        }

        activity?.SetTag("job_name", jobName);

        // 1. Send CancelChat — best-effort
        await TrySendCancelChatAsync(agentId, session, jobName, cancellationToken);

        // 2. Wait up to 10s for the watcher to confirm terminal
        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        graceCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await session.WatcherTask.WaitAsync(graceCts.Token);
            activity?.SetTag(TagOutcome, "clean");
        }
        catch (OperationCanceledException)
        {
            activity?.SetTag(TagOutcome, "force_delete");
            await ForceDeleteAndCleanupAsync(agentId, jobName, session, cancellationToken);
        }
    }

    private async Task TrySendCancelChatAsync(
        string agentId, ChatSession session, string jobName, CancellationToken ct)
    {
        var agentEntry = _registry.GetByAgentId(agentId);
        if (agentEntry is null)
            return;

        try
        {
            await _hubContext.Clients.Client(agentEntry.ConnectionId)
                .CancelChat(session.DispatchId.ToString());

            _logger.Information(
                "ChatJobDispatcher: CancelChat sent to agent {AgentId} for job {JobName}",
                agentId, jobName);
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "ChatJobDispatcher: CancelChat to agent {AgentId} failed (will await watcher): {ErrorMessage}",
                agentId, ex.Message);
        }
    }

    private async Task ForceDeleteAndCleanupAsync(
        string agentId, string jobName, ChatSession session, CancellationToken cancellationToken)
    {
        // Cancel watcher first so it exits without running its own cleanup
        try { await session.WatcherCts.CancelAsync(); } catch (OperationCanceledException) { }

        _logger.Warning(
            "ChatJobDispatcher: grace period expired for {JobName} — force deleting job",
            jobName);

        try
        {
            await _jobClient.DeleteJobAsync(jobName, _options.Namespace, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "ChatJobDispatcher: force delete failed for {JobName}: {ErrorMessage}",
                jobName, ex.Message);
        }

        // Gate all cleanup + metrics inside TryRemove — prevents double-decrement
        // if WatchJobUntilTerminalAsync fired terminal cleanup concurrently
        if (_sessions.TryRemove(jobName, out var removed))
        {
            _agentIdToJobName.TryRemove(agentId, out _);

            var selectorTag = new KeyValuePair<string, object?>(
                TagAgentSelector, removed.NormalizedSelector.Replace(',', '_'));
            ChatTelemetry.PodForceTerminations.Add(1, selectorTag);
            ChatTelemetry.SessionsActive.Add(-1, selectorTag);
            if (removed.ClaimedPvc is not null)
                ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));

            var duration = (DateTimeOffset.UtcNow - removed.ConnectedAt).TotalSeconds;
            ChatTelemetry.SessionDuration.Record(
                duration,
                selectorTag,
                new KeyValuePair<string, object?>(TagOutcome, "force_deleted"));

            try { removed.WatcherCts.Dispose(); } catch { /* Intentional: CTS may already be disposed if watcher fired concurrently during force-delete path. */ }
        }
    }

    // ─── IAsyncDisposable ─────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private async Task TryCleanupFailedDispatch(
        string jobName, CancellationToken ct)
    {
        try { await _jobClient.DeleteJobAsync(jobName, _options.Namespace, ct); }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ChatJobDispatcher: cleanup delete failed for {JobName}", jobName);
        }
    }

    private static bool IsTerminal(V1Job job)
        => job.Status?.Conditions?.Any(c =>
               (c.Type == "Complete" || c.Type == "Failed") && c.Status == "True") == true;

    private static bool IsKiroAgent(string providerType)
        => string.Equals(providerType, "kiro", StringComparison.OrdinalIgnoreCase);

    private static bool IsOpencodeAgent(string providerType)
        => string.Equals(providerType, "opencode", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> ValidEffortValues =
        new(["high", "medium", "low"], StringComparer.OrdinalIgnoreCase);

    [System.Text.RegularExpressions.GeneratedRegex(@"^[a-zA-Z0-9._\-]{1,63}$")]
    private static partial System.Text.RegularExpressions.Regex K8sLabelValuePattern();
}
