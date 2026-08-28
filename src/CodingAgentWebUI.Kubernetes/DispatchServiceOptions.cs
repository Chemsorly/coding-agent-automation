namespace CodingAgentWebUI.Kubernetes;

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

    /// <summary>ServiceAccount name for agent Job pods (zero RBAC).</summary>
    public string AgentServiceAccountName { get; set; } = "";

    /// <summary>K8s namespace for Job creation.</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>K8s Secret name containing opencode config file (mounted for opencode agents).</summary>
    public string OpencodeConfigSecretName { get; set; } = "";

    /// <summary>
    /// Maximum agent job lifetime in seconds. Sets <c>activeDeadlineSeconds</c> on every K8s Job pod
    /// (work-item agent jobs, consolidation jobs, and chat session pods). Default: 7200.
    /// </summary>
    public int AgentJobTimeoutSeconds { get; set; } = 7200;

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

    private const int MinAgentJobTimeoutSeconds = 60;
    private const int MinChatPodConnectTimeoutSeconds = 5;
    private const int MinChatTerminationGracePeriodSeconds = 5;
    private const int MinChatIdleTimeoutSeconds = 10;

    /// <summary>
    /// Validates job timeout and chat-related config values, clamping to safe minimums.
    /// Called after options binding to prevent zero/negative values that would
    /// immediately kill or never start agent pods.
    /// </summary>
    public void ValidateAndClamp(Serilog.ILogger? logger = null)
    {
        if (AgentJobTimeoutSeconds < MinAgentJobTimeoutSeconds)
        {
            logger?.Warning("AgentJobTimeoutSeconds ({Value}) is below minimum ({Min}), clamping",
                AgentJobTimeoutSeconds, MinAgentJobTimeoutSeconds);
            AgentJobTimeoutSeconds = MinAgentJobTimeoutSeconds;
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
    }
}
