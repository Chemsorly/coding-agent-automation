namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A consolidation job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingConsolidationJob
{
    public required string RunId { get; init; }
    public required ConsolidationRunType Type { get; init; }
    public string? TemplateId { get; init; }
    public string? TemplateName { get; init; }
    public required string WorkspacePath { get; init; }
    public required IReadOnlyList<string> RequiredLabels { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public int RetryCount { get; set; }

    /// <summary>Maximum dispatch retry attempts before marking the run as Failed.</summary>
    public const int MaxRetries = 5;
}
