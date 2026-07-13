using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Intermediate result of the orchestration phase, containing all resolved context
/// needed by <see cref="Pipeline.Interfaces.IWorkDistributor"/> implementations to
/// build their delivery-specific representation (WorkItem payload or JobAssignmentMessage).
/// </summary>
public sealed record DispatchPreparationResult
{
    /// <summary>Resolved agent profile for this dispatch.</summary>
    public required AgentProfile ResolvedProfile { get; init; }

    /// <summary>Quality gate configurations applicable to this dispatch.</summary>
    public required IReadOnlyList<QualityGateConfiguration> QualityGateConfigs { get; init; }

    /// <summary>Reviewer configurations applicable to this dispatch.</summary>
    public required IReadOnlyList<ReviewerConfiguration> ReviewerConfigs { get; init; }

    /// <summary>Provider configs prepared for the agent (tokens vended, secrets excluded).</summary>
    public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }

    /// <summary>Pipeline configuration snapshot with project/template overrides applied.</summary>
    public required PipelineConfiguration PipelineConfiguration { get; init; }

    /// <summary>Full issue details fetched from the issue provider.</summary>
    public required IssueDetail IssueDetail { get; init; }

    /// <summary>Parsed issue structure (requirements, acceptance criteria, etc.).</summary>
    public required ParsedIssue ParsedIssue { get; init; }

    /// <summary>Issue comments fetched at orchestration time.</summary>
    public required IReadOnlyList<IssueComment> IssueComments { get; init; }

    /// <summary>Existing analysis content from a previous run, if any.</summary>
    public string? ExistingAnalysis { get; init; }

    /// <summary>Whether existing analysis should be refreshed (e.g., after gate rejection).</summary>
    public bool ForceRefreshAnalysis { get; init; }

    /// <summary>Which staleness signal triggered force-refresh (null if not staleness-triggered).</summary>
    public string? StalenessSignal { get; init; }

    /// <summary>Number of prior analysis refreshes for this issue (for OTel).</summary>
    public int AnalysisRefreshCount { get; init; }

    /// <summary>The PipelineRun created and registered for this dispatch.</summary>
    public required PipelineRun CreatedRun { get; init; }

    /// <summary>The project this dispatch belongs to.</summary>
    public required PipelineProject Project { get; init; }

    /// <summary>MCP server configs from the resolved profile.</summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }

    /// <summary>Trace context for distributed tracing propagation.</summary>
    public Dictionary<string, string>? TraceContext { get; init; }
}
