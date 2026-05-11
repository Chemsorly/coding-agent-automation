using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

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

    /// <summary>For harness suggestions: serialized RunFeedback entries as JSON.</summary>
    [Key(7)]
    public string? FeedbackDataJson { get; init; }

    /// <summary>
    /// The workspace path for the consolidation run, as determined by the orchestrator.
    /// Executors should use this path instead of constructing their own temp paths.
    /// </summary>
    [Key(8)]
    public string? WorkspacePath { get; init; }
}
