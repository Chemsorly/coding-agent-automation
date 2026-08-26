using System.ComponentModel.DataAnnotations;

namespace CodingAgentWebUI;

/// <summary>
/// Runtime configuration for the orchestrator (monolith) host, bound from environment variables.
/// T11 (arch-audit 2026-08-22): replaces scattered <c>Environment.GetEnvironmentVariable</c> calls.
/// </summary>
public sealed class MonolithRuntimeOptions
{
    public const string SectionName = "Orchestrator";

    /// <summary>
    /// Seconds to wait after /readyz returns 503 before proceeding with shutdown.
    /// Allows endpoint removal to propagate before active connections are dropped.
    /// Env: <c>READINESS_DRAIN_DELAY_SECONDS</c>. Default: 15. Range: 0–120.
    /// </summary>
    [Range(0, 120, ErrorMessage = "READINESS_DRAIN_DELAY_SECONDS must be between 0 and 120.")]
    public int ReadinessDrainDelaySeconds { get; set; } = 15;

    /// <summary>
    /// Seconds to wait after host start before the pipeline loop auto-resumes.
    /// Allows the previous pod to drain before the new leader starts dispatching.
    /// Env: <c>PIPELINE_LOOP_STARTUP_DELAY_SECONDS</c>. Default: 0 (Spec 044 Req 7.2). Range: 0–300.
    /// </summary>
    [Range(0, 300, ErrorMessage = "PIPELINE_LOOP_STARTUP_DELAY_SECONDS must be between 0 and 300.")]
    public int PipelineLoopStartupDelaySeconds { get; set; } = 0;

    /// <summary>
    /// API key for outbound Pipeline API calls (used by <c>PipelineApiClientOptions</c>).
    /// Env: <c>AGENT_API_KEY</c>.
    /// </summary>
    public string AgentApiKey { get; set; } = "";
}
