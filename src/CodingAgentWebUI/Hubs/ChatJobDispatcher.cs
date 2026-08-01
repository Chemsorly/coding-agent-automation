using System.Collections.Concurrent;
using System.Diagnostics;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration.Dispatch;
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
public sealed class ChatJobDispatcher : IHostedService, IAsyncDisposable, IChatJobDispatcher
{
    private readonly IKubernetesJobClient _jobClient;
    private readonly IHubContext<AgentHub, IAgentHubClient> _hubContext;
    private readonly JobTemplateStore _templateStore;
    private readonly AgentRegistryService _registry;
    private readonly DispatchServiceOptions _options;
    private readonly ILogger _logger;

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
        ILogger logger)
    {
        _jobClient = jobClient;
        _hubContext = hubContext;
        _templateStore = templateStore;
        _registry = registry;
        _options = options;
        _logger = logger;
    }

    // ─── DispatchChatPodAsync ──────────────────────────────────────────────────

    public async Task<string> DispatchChatPodAsync(
        string agentSelector, string? model, string? effort, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Dispatch");

        var normalized = JobTemplateStore.NormalizeLabels(agentSelector);
        var selectorLabelValue = normalized.Replace(',', '_');

        activity?.SetTag("agent_selector", normalized);

        // Query all active chat jobs — used for both double-dispatch guard and PVC availability.
        // Using a single broad query (label key presence, no value filter) is replica-safe:
        // each orchestrator replica sees the full cluster state rather than its own in-memory sessions.
        var allChatJobs = await _jobClient.ListJobsAsync(
            _options.Namespace, "caa/chat-session-id", ct);

        var activeChatJobs = allChatJobs.Items?
            .Where(j => !IsTerminal(j))
            .ToList() ?? [];

        // Double-dispatch guard: reject if a non-terminal job already exists for this selector
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

        // Step 3: get template
        var template = _templateStore.Resolve(normalized)
            ?? throw new InvalidOperationException($"No template for selector '{normalized}'");

        var jobName = $"caa-chat-{Guid.NewGuid().ToString("N")[..8]}";
        var dispatchId = Guid.NewGuid();
        var dispatchStart = DateTimeOffset.UtcNow;

        // Step 5: PVC claim for kiro agents — replica-safe: read from k8s labels, not in-memory dict
        string? claimedPvc = null;
        if (IsKiroAgent(template.ProviderType))
        {
            var claimedByActiveJobs = activeChatJobs
                .Select(j =>
                {
                    var labels = j.Metadata?.Labels;
                    return labels is not null && labels.TryGetValue("caa/claimed-pvc", out var p) ? p : null;
                })
                .Where(p => p is not null)
                .ToHashSet(StringComparer.Ordinal)!;

            claimedPvc = _options.KiroPvcPool.FirstOrDefault(p => !claimedByActiveJobs.Contains(p));
            if (claimedPvc is null)
                throw new NoPvcAvailableException();
        }

        // Step 6: build context
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

        // Step 7: build job
        var job = JobSpecBuilder.Build(template, ctx);

        // Post-build: inject env vars
        var container = job.Spec.Template.Spec.Containers[0];
        container.Env ??= new List<V1EnvVar>();
        container.Env.Add(new V1EnvVar { Name = "AGENT_CHAT_MODE", Value = "true" });
        container.Env.Add(new V1EnvVar { Name = "AGENT_CHAT_SESSION_ID", Value = dispatchId.ToString() });

        if (!string.IsNullOrEmpty(model) && !model.Equals("auto", StringComparison.OrdinalIgnoreCase))
            container.Env.Add(new V1EnvVar { Name = "AGENT_CHAT_MODEL", Value = model });

        if (!string.IsNullOrEmpty(effort) && !effort.Equals("auto", StringComparison.OrdinalIgnoreCase))
            container.Env.Add(new V1EnvVar { Name = "AGENT_CHAT_EFFORT", Value = effort });

        // Post-build: set labels
        job.Metadata.Labels ??= new Dictionary<string, string>();
        job.Metadata.Labels["caa/chat-session-id"] = dispatchId.ToString();
        job.Metadata.Labels["caa/chat-selector"] = selectorLabelValue;
        if (claimedPvc is not null)
            job.Metadata.Labels["caa/claimed-pvc"] = claimedPvc;

        // Post-build: override spec fields
        job.Spec.BackoffLimit = 0;
        job.Spec.ActiveDeadlineSeconds = _options.ChatSessionMaxDurationSeconds;
        job.Spec.Template.Spec.TerminationGracePeriodSeconds = _options.ChatTerminationGracePeriodSeconds;

        // Step 8: create job
        await _jobClient.CreateJobAsync(job, _options.Namespace, ct);

        _logger.Information(
            "ChatJobDispatcher: dispatched chat pod {JobName} for selector {AgentSelector} (dispatchId={DispatchId}, pvc={Pvc})",
            jobName, normalized, dispatchId, claimedPvc ?? "none");

        activity?.SetTag("dispatch_id", dispatchId.ToString());
        activity?.SetTag("job_name", jobName);
        activity?.SetTag("model", model ?? "auto");
        activity?.SetTag("effort", effort ?? "auto");
        activity?.SetTag("provider_type", template.ProviderType);

        // Step 9: poll for agent connection
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
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

                    // Record dispatch latency metric
                    var tag = new KeyValuePair<string, object?>("agent_selector", selectorLabelValue);
                    ChatTelemetry.DispatchLatency.Record(elapsed, tag);

                    return connected.AgentId;
                }

                await Task.Delay(2000, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our internal timeout — not outer cancellation
            _logger.Warning(
                "ChatJobDispatcher: chat pod for {AgentSelector} did not connect within {TimeoutSeconds}s — cleaning up {JobName}",
                normalized, _options.ChatPodConnectTimeoutSeconds, jobName);

            var tag = new KeyValuePair<string, object?>("agent_selector", selectorLabelValue);
            ChatTelemetry.PodConnectTimeouts.Add(1, tag);

            activity?.SetStatus(ActivityStatusCode.Error, "Connect timeout");

            await TryCleanupFailedDispatch(jobName, ct);
            throw new ChatPodTimeoutException(_options.ChatPodConnectTimeoutSeconds);
        }
        catch (OperationCanceledException)
        {
            // Outer cancellation (app shutdown)
            activity?.SetStatus(ActivityStatusCode.Error, "Cancelled");
            await TryCleanupFailedDispatch(jobName, CancellationToken.None);
            throw;
        }

        // Unreachable — loop exits only via cancellation
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
            WatcherTask = Task.Run(() => WatchJobUntilTerminalAsync(jobName, claimedPvc, selector, cts.Token))
        };
        _sessions[jobName] = session;

        // Secondary index: only when agentId is known (non-empty) — restart recovery passes ""
        if (!string.IsNullOrEmpty(agentId))
            _agentIdToJobName[agentId] = jobName;

        // Metrics
        var selectorTag = new KeyValuePair<string, object?>("agent_selector", selector.Replace(',', '_'));
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
                // Cancelled (StopAsync) — caller handles PVC release
                return;
            }

            V1Job? job = null;
            try
            {
                job = await _jobClient.ReadJobAsync(jobName, _options.Namespace, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.Warning(
                    "ChatJobDispatcher: transient ReadJobAsync failure for {JobName} (will retry): {ErrorMessage}",
                    jobName, ex.Message);
                continue;
            }

            if (job is null || IsTerminal(job))
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

                // Release PVC
                if (claimedPvc is not null)
                {
                    ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));
                }

                // Update metrics
                var selectorTag = new KeyValuePair<string, object?>("agent_selector", selectorEncoded);
                ChatTelemetry.SessionsActive.Add(-1, selectorTag);

                if (_sessions.TryRemove(jobName, out var removed))
                {
                    var duration = (DateTimeOffset.UtcNow - removed.ConnectedAt).TotalSeconds;
                    ChatTelemetry.SessionDuration.Record(
                        duration,
                        selectorTag,
                        new KeyValuePair<string, object?>("outcome", "completed"));

                    if (!string.IsNullOrEmpty(removed.AgentId))
                        _agentIdToJobName.TryRemove(removed.AgentId, out _);

                    removed.WatcherCts.Dispose();
                }

                return;
            }
        }
        // Cancellation path: caller (StopAsync) releases PVC
    }

    // ─── IHostedService ───────────────────────────────────────────────────────

    /// <summary>
    /// Restores in-memory watcher state for any active chat jobs after orchestrator restart.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            var jobs = await _jobClient.ListJobsAsync(_options.Namespace, "caa/chat-session-id", ct);
            foreach (var job in (jobs.Items ?? []).Where(j => !IsTerminal(j)))
            {
                try
                {
                    var labels = job.Metadata?.Labels ?? new Dictionary<string, string>();
                    labels.TryGetValue("caa/chat-session-id", out var sessionIdStr);
                    var sessionId = Guid.Parse(sessionIdStr ?? "");
                    labels.TryGetValue("caa/claimed-pvc", out var pvcLabel);
                    labels.TryGetValue("caa/chat-selector", out var selectorLabel);

                    // agentId not known on restart — leave empty; watcher will still release PVC
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

    public async Task StopAsync(CancellationToken ct)
    {
        var sessions = _sessions.Values.ToList();
        _logger.Information("ChatJobDispatcher: stopping — releasing {Count} active session(s)", sessions.Count);

        foreach (var session in sessions)
            session.WatcherCts.Cancel();

        // Await watchers with 5s timeout — swallow any exception/timeout
        try
        {
            await Task.WhenAll(sessions.Select(s => s.WatcherTask))
                .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        }
        catch
        {
            // Timeout or aggregate — fall through to manual cleanup
        }

        // Clean up sessions whose watchers didn't complete
        foreach (var session in sessions)
        {
            if (_sessions.TryRemove(session.JobName, out _))
            {
                if (!string.IsNullOrEmpty(session.AgentId))
                    _agentIdToJobName.TryRemove(session.AgentId, out _);
            }

            try { session.WatcherCts.Dispose(); } catch { /* already disposed */ }
        }
    }

    // ─── TerminateChatSessionAsync ────────────────────────────────────────────

    public async Task TerminateChatSessionAsync(string agentId, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Chat.Terminate");
        activity?.SetTag("agent_id", agentId);

        if (!_agentIdToJobName.TryGetValue(agentId, out var jobName))
        {
            activity?.SetTag("outcome", "not_found");
            return;
        }

        if (!_sessions.TryGetValue(jobName, out var session))
        {
            activity?.SetTag("outcome", "not_found");
            return;
        }

        activity?.SetTag("job_name", jobName);

        // 1. Send CancelChat — best-effort
        var agentEntry = _registry.GetByAgentId(agentId);
        if (agentEntry is not null)
        {
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

        // 2. Wait up to 10s for the watcher to confirm terminal
        using var graceCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        graceCts.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            await session.WatcherTask.WaitAsync(graceCts.Token);
            activity?.SetTag("outcome", "clean");
        }
        catch (OperationCanceledException)
        {
            // Grace period expired — force delete
            _logger.Warning(
                "ChatJobDispatcher: grace period expired for {JobName} — force deleting and releasing PVC {Pvc}",
                jobName, session.ClaimedPvc ?? "none");

            activity?.SetTag("outcome", "force_delete");

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

            // Record metrics
            var selectorTag = new KeyValuePair<string, object?>(
                "agent_selector", session.NormalizedSelector.Replace(',', '_'));
            ChatTelemetry.PodForceTerminations.Add(1, selectorTag);
            ChatTelemetry.SessionsActive.Add(-1, selectorTag);
            if (session.ClaimedPvc is not null)
                ChatTelemetry.PvcUtilization.Add(-1, new KeyValuePair<string, object?>("pool", "kiro"));

            var duration = (DateTimeOffset.UtcNow - session.ConnectedAt).TotalSeconds;
            ChatTelemetry.SessionDuration.Record(
                duration,
                selectorTag,
                new KeyValuePair<string, object?>("outcome", "force_deleted"));

            _sessions.TryRemove(jobName, out _);
            _agentIdToJobName.TryRemove(agentId, out _);

            try { session.WatcherCts.Cancel(); } catch { }
            try { session.WatcherCts.Dispose(); } catch { }
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
}
