namespace CodingAgent.Kubernetes;

/// <summary>
/// Configuration options for DispatchService.
/// Bound from "WorkDistribution:Dispatch" and related sections.
/// </summary>
public sealed class DispatchServiceOptions
{
    /// <summary>Interval between poll cycles in seconds. Default: 10.</summary>
    public int PollIntervalSeconds { get; set; } = 10;

    /// <summary>Maximum Job creations per second. Default: 10.</summary>
    public int RateLimitPerSecond { get; set; } = 10;

    /// <summary>PVC names for kiro agent credential pool.</summary>
    public List<string> KiroPvcPool { get; set; } = [];

    /// <summary>Orchestrator URL injected into Job pods (e.g., http://caa-orchestrator:5000).</summary>
    public string OrchestratorUrl { get; set; } = "";

    /// <summary>K8s Secret name containing the master agent API key (for OTEL headers mount only; NOT vended to agent pods).</summary>
    public string AgentApiKeySecretName { get; set; } = "";

    /// <summary>
    /// The master agent API key value, read from the <c>AGENT_API_KEY</c> environment variable at startup.
    /// Used by <see cref="CodingAgent.Api.Dispatch.DispatchLifecycleService"/> to pre-compute
    /// per-job credentials (<c>HMAC-SHA256(masterKey, jobName)</c>) stored in per-job K8s Secrets.
    /// Never injected into agent pods directly — only the derived per-job value enters the pod.
    /// </summary>
    // TODO: This property holds the raw master key as a plain string. If DispatchServiceOptions is
    // ever passed to a structured logger with destructuring (e.g., {@options}), or bound to an ASP.NET
    // configuration diagnostics endpoint, the master key may be exposed in plaintext. Consider
    // annotating with [LogMasked] / a redaction attribute, or storing only a flag indicating whether
    // the key is present rather than the key value itself.
    public string AgentApiKeyValue { get; set; } = "";

    /// <summary>ServiceAccount name for agent Job pods (zero RBAC).</summary>
    public string AgentServiceAccountName { get; set; } = "";

    /// <summary>K8s namespace for Job creation.</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>K8s Secret name containing opencode config file (mounted for opencode agents).</summary>
    public string OpencodeConfigSecretName { get; set; } = "";

    /// <summary>
    /// Maximum chat session pod lifetime in seconds. Sets <c>activeDeadlineSeconds</c> on chat
    /// session K8s Job pods. Does NOT apply to work-item agent jobs (those use
    /// <c>PipelineConfiguration.AgentTimeout</c> per-project). Default: 7200.
    /// </summary>
    public int ChatJobMaxDurationSeconds { get; set; } = 7200;

    /// <summary>Time to wait for chat pod to connect before aborting. Default: 120s.</summary>
    public int ChatPodConnectTimeoutSeconds { get; set; } = 120;

    /// <summary>terminationGracePeriodSeconds on chat Job pod spec. Default: 120s.</summary>
    public int ChatTerminationGracePeriodSeconds { get; set; } = 120;

    /// <summary>
    /// Maximum seconds a chat pod may remain idle (no client keepalive heartbeat) before the
    /// watcher terminates it automatically. The Blazor UI sends a heartbeat every
    /// <c>ChatKeepaliveIntervalSeconds</c> while the chat window is open; pods whose window has
    /// been closed or crashed are cleaned up within this window. Default: 90s.
    /// </summary>
    public int ChatIdleTimeoutSeconds { get; set; } = 90;

    /// <summary>
    /// Number of API replicas. Used by <see cref="CodingAgent.Orchestration.Dispatch.ChatJobDispatcher"/>
    /// to emit a startup warning when Redis is absent and replicas &gt; 1, indicating that keepalive
    /// heartbeats may be silently lost on non-watcher replicas. Default: 1 (safe for local dev / single-replica).
    /// </summary>
    // TODO: Add a lower-bound clamp for ChatReplicaCount in ValidateAndClamp (Math.Max(1, value)), consistent
    // with the other numeric options (ChatJobMaxDurationSeconds, ChatIdleTimeoutSeconds, etc.). A misconfigured
    // value of 0 or negative (e.g. via Helm --set api.replicas=0) evaluates as <= 1, silently suppressing
    // the Redis warning even though the deployment is broken. See review finding [WARNING] #2133.
    public int ChatReplicaCount { get; set; } = 1;

    private const int MinChatJobMaxDurationSeconds = 60;
    private const int MinChatPodConnectTimeoutSeconds = 5;
    private const int MinChatTerminationGracePeriodSeconds = 5;
    private const int MinChatIdleTimeoutSeconds = 10;

    /// <summary>
    /// Grace window in seconds for WorkItems with a null <c>DispatchedAt</c> timestamp.
    /// After this window expires (measured from <c>CreatedAt</c>), the item is force-failed
    /// by <c>ReconciliationLoop.EnforceTimeoutsAsync</c> using <c>CreatedAt</c> as the
    /// timeout anchor.
    /// Must be at least <c>TimeoutCanaryMinAgeSeconds</c> (60s) so that once the grace window
    /// is exceeded and the item's execution age is set to <c>createdAgeSeconds</c>, the canary
    /// guard (<c>executionAgeSeconds &lt; 60s</c>) cannot re-fire and re-skip the item.
    /// Default: 3600s (2× <c>PipelineConstants.DefaultAgentTimeout</c> of 1800s).
    /// </summary>
    public int NullDispatchedAtGraceWindowSeconds { get; set; } = 3600;

    private const int MinNullDispatchedAtGraceWindowSeconds = 60;

    /// <summary>
    /// Validates chat-related config values, clamping to safe minimums.
    /// Called after options binding to prevent zero/negative values that would
    /// immediately kill or never start chat pods.
    /// </summary>
    public void ValidateAndClamp(Serilog.ILogger? logger = null)
    {
        if (ChatJobMaxDurationSeconds < MinChatJobMaxDurationSeconds)
        {
            logger?.Warning("ChatJobMaxDurationSeconds ({Value}) is below minimum ({Min}), clamping",
                ChatJobMaxDurationSeconds, MinChatJobMaxDurationSeconds);
            ChatJobMaxDurationSeconds = MinChatJobMaxDurationSeconds;
        }
        if (ChatPodConnectTimeoutSeconds < MinChatPodConnectTimeoutSeconds)
        {
            logger?.Warning("ChatPodConnectTimeoutSeconds ({Value}) is below minimum ({Min}), clamping",
                ChatPodConnectTimeoutSeconds, MinChatPodConnectTimeoutSeconds);
            ChatPodConnectTimeoutSeconds = MinChatPodConnectTimeoutSeconds;
        }
        if (ChatTerminationGracePeriodSeconds < MinChatTerminationGracePeriodSeconds)
        {
            logger?.Warning("ChatTerminationGracePeriodSeconds ({Value}) is below minimum ({Min}), clamping",
                ChatTerminationGracePeriodSeconds, MinChatTerminationGracePeriodSeconds);
            ChatTerminationGracePeriodSeconds = MinChatTerminationGracePeriodSeconds;
        }
        if (ChatIdleTimeoutSeconds < MinChatIdleTimeoutSeconds)
        {
            logger?.Warning("ChatIdleTimeoutSeconds ({Value}) is below minimum ({Min}), clamping",
                ChatIdleTimeoutSeconds, MinChatIdleTimeoutSeconds);
            ChatIdleTimeoutSeconds = MinChatIdleTimeoutSeconds;
        }
        if (NullDispatchedAtGraceWindowSeconds < MinNullDispatchedAtGraceWindowSeconds)
        {
            logger?.Warning("NullDispatchedAtGraceWindowSeconds ({Value}) is below minimum ({Min}), clamping",
                NullDispatchedAtGraceWindowSeconds, MinNullDispatchedAtGraceWindowSeconds);
            NullDispatchedAtGraceWindowSeconds = MinNullDispatchedAtGraceWindowSeconds;
        }
    }
}
