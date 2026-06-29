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

    /// <summary>
    /// Max concurrent pods per agent selector group.
    /// Key: sorted comma-joined selector (e.g., "dotnet,kiro"). Value: max concurrent.
    /// Empty or missing key = no limit.
    /// </summary>
    public Dictionary<string, int> MaxConcurrentPods { get; set; } = new();

    /// <summary>
    /// Image mapping: sorted comma-joined agent labels → container image.
    /// e.g., "kiro,dotnet,dotnet10" → "chemsorly/coding-agent:coding-agent-kiro-dotnet10"
    /// </summary>
    public Dictionary<string, string> ImageMapping { get; set; } = new();

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

    /// <summary>Resource requests/limits for Job containers.</summary>
    public JobResourceConfig? JobResources { get; set; }
}

/// <summary>Resource configuration for Job containers.</summary>
public sealed class JobResourceConfig
{
    public Dictionary<string, string>? Requests { get; set; }
    public Dictionary<string, string>? Limits { get; set; }
}
