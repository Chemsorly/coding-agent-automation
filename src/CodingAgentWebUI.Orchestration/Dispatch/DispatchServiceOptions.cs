namespace CodingAgentWebUI.Orchestration.Dispatch;

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

    /// <summary>K8s Secret name containing the agent API key.</summary>
    public string AgentApiKeySecretName { get; set; } = "";

    /// <summary>ServiceAccount name for agent Job pods (zero RBAC).</summary>
    public string AgentServiceAccountName { get; set; } = "";

    /// <summary>K8s namespace for Job creation.</summary>
    public string Namespace { get; set; } = "default";

    /// <summary>K8s Secret name containing opencode config file (mounted for opencode agents).</summary>
    public string OpencodeConfigSecretName { get; set; } = "";

    /// <summary>Maximum chat session lifetime in seconds. Sets activeDeadlineSeconds on the K8s Job. Default: 7200.</summary>
    public int ChatSessionMaxDurationSeconds { get; set; } = 7200;

    /// <summary>Time to wait for chat pod to connect before aborting. Default: 120s.</summary>
    public int ChatPodConnectTimeoutSeconds { get; set; } = 120;

    /// <summary>terminationGracePeriodSeconds on chat Job pod spec. Default: 120s.</summary>
    public int ChatTerminationGracePeriodSeconds { get; set; } = 120;
}
