namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Configuration for multi-agent routing, brain sync, and output buffering.
/// </summary>
public sealed record AgentConfiguration
{
    /// <summary>
    /// Global fallback for agent label routing when a repository's ProviderConfig
    /// does not specify <c>requiredAgentLabels</c>. Comma-separated string (e.g., "kiro,dotnet").
    /// Null means any idle agent can be selected.
    /// </summary>
    public string? DefaultRequiredAgentLabels { get; init; }

    /// <summary>
    /// Maximum number of retry attempts when brain repo push fails with a non-fast-forward error
    /// (concurrent push conflict). Each retry fetches, rebases, resolves conflicts, and retries push.
    /// Default: 3.
    /// </summary>
    public int BrainPushMaxRetries { get; init; } = 3;

    /// <summary>
    /// How long to wait after an agent disconnects before marking its active run as Failed.
    /// Default: 5 minutes.
    /// </summary>
    public TimeSpan AgentDisconnectGracePeriod { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum number of output lines to retain per active pipeline run (ring buffer capacity).
    /// Default: 10,000.
    /// </summary>
    public int OutputBufferCapacity { get; init; } = PipelineConstants.DefaultOutputBufferCapacity;

    /// <summary>
    /// When true, the brain repository operates in read-only mode: pre-run sync
    /// (clone/pull) and context injection proceed normally, but all write operations
    /// are skipped — write instructions are omitted from the prompt, validation is
    /// skipped, and the SyncingBrainRepoPostRun step (commit and push) is skipped
    /// entirely. Defaults to false.
    /// </summary>
    public bool BrainReadOnly { get; init; } = false;
}
