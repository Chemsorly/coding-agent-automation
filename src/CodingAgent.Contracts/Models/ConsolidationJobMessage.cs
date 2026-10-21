using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Orchestrator → Agent: Job assignment for a consolidation run.
/// Sent via SignalR when a consolidation job is dispatched to an agent worker.
/// </summary>
[MessagePackObject]
public sealed class ConsolidationJobMessage
{
    [Key(0)]
    public required string JobId { get; init; }

    [Key(1)]
    public required ConsolidationRunType Type { get; init; }

    [Key(2)]
    public string? TemplateId { get; init; }

    [Key(3)]
    public string? TemplateName { get; init; }

    [Key(4)]
    public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }

    [Key(5)]
    public required PipelineConfiguration PipelineConfiguration { get; init; }

    /// <summary>Timestamp of last successful consolidation of this type for this template.</summary>
    [Key(6)]
    public DateTime? LastSuccessfulRunUtc { get; init; }

    // Key(7) is retired (was FeedbackDataJson). Do not reuse.

    // Key(8) is retired (was WorkspacePath; the agent now chooses the workspace itself). Do not reuse.

    /// <summary>
    /// W3C trace context (traceparent, tracestate) injected at dispatch time.
    /// Used by the agent to create a child span linked to the orchestrator's trace.
    /// Null when the orchestrator has no active trace or for backward compatibility.
    /// </summary>
    [Key(9)]
    public Dictionary<string, string>? TraceContext { get; init; }

    /// <summary>
    /// When true, created refactoring issues will receive both <c>agent:generated</c> and
    /// <c>agent:next</c> labels. Defaults to <c>false</c> for backward compatibility.
    /// </summary>
    [Key(10)]
    public bool AutoDispatch { get; init; }

    /// <summary>
    /// The MCP servers of the run's agent profile and project, written to the agent's MCP config before
    /// the consolidation agents run, as for pipeline runs.
    /// </summary>
    [Key(11)]
    public IReadOnlyList<McpServerConfig> McpServers { get; init; } = [];

    /// <summary>
    /// Which detection agent a refactoring scan runs. Defaults to <see cref="RefactoringScanFocus.All"/>
    /// for backward compatibility, so messages written before this field existed run every agent.
    /// </summary>
    [Key(12)]
    public RefactoringScanFocus RefactoringFocus { get; init; }
}
