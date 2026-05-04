namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for multi-agent behavior and brain repository.
/// </summary>
public sealed record AgentConfiguration
{
    /// <summary>
    /// Global fallback for agent label routing when a repository's ProviderConfig
    /// does not specify requiredAgentLabels. Null means any idle agent can be selected.
    /// </summary>
    public string? DefaultRequiredAgentLabels { get; init; }

    /// <summary>
    /// Maximum number of retry attempts when brain repo push fails with a non-fast-forward error.
    /// </summary>
    public int BrainPushMaxRetries { get; init; } = 3;

    /// <summary>
    /// How long to wait after an agent disconnects before marking its active run as Failed.
    /// </summary>
    public TimeSpan AgentDisconnectGracePeriod { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of output lines to retain per active pipeline run (ring buffer capacity).
    /// </summary>
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;

    /// <summary>
    /// When true, the brain repository operates in read-only mode.
    /// </summary>
    public bool BrainReadOnly { get; init; } = false;
}
