namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Serialized to WorkItems.Payload (JSONB). Contains the full context needed
/// by an agent to execute a work item. Does NOT contain per-project runtime secrets.
/// </summary>
public record JobDistributionRequest
{
    /// <summary>Issue identifier (e.g., "owner/repo#123").</summary>
    public required string IssueIdentifier { get; init; }

    /// <summary>ID of the issue provider config used to fetch this issue.</summary>
    public required string IssueProviderConfigId { get; init; }

    /// <summary>ID of the repository provider config for the work target.</summary>
    public required string RepoProviderConfigId { get; init; }

    /// <summary>ID of the brain provider config, if applicable.</summary>
    public string? BrainProviderConfigId { get; init; }

    /// <summary>ID of the pipeline provider config, if applicable.</summary>
    public string? PipelineProviderConfigId { get; init; }

    /// <summary>User or system identity that initiated this work item.</summary>
    public required string InitiatedBy { get; init; }

    /// <summary>Type of work to perform.</summary>
    public required WorkItemTaskType TaskType { get; init; }

    /// <summary>Sorted comma-joined agent labels for dispatch routing.</summary>
    public required string AgentSelector { get; init; }

    /// <summary>Maximum execution time in seconds before timeout.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Project ID this work item belongs to, if any.</summary>
    public string? ProjectId { get; init; }

    /// <summary>Project display name, if any.</summary>
    public string? ProjectName { get; init; }

    /// <summary>Pipeline run type discriminator.</summary>
    public PipelineRunType RunType { get; init; }

    // --- Full issue context (pre-fetched at enqueue time) ---

    /// <summary>Full issue details fetched from the issue provider.</summary>
    public IssueDetail? IssueDetail { get; init; }

    /// <summary>Parsed issue structure (requirements, acceptance criteria, etc.).</summary>
    public ParsedIssue? ParsedIssue { get; init; }

    /// <summary>Issue comments at time of enqueue.</summary>
    public IReadOnlyList<IssueComment>? IssueComments { get; init; }

    /// <summary>Existing analysis content from a previous run, if any.</summary>
    public string? ExistingAnalysis { get; init; }

    // --- Provider configs (serialized without secrets) ---

    /// <summary>Provider configurations relevant to this work item.</summary>
    public IReadOnlyList<ProviderConfig>? ProviderConfigs { get; init; }

    /// <summary>Pipeline configuration snapshot at enqueue time.</summary>
    public PipelineConfiguration? PipelineConfiguration { get; init; }

    /// <summary>Resolved agent profile ID for this work item.</summary>
    public string? ResolvedProfileId { get; init; }

    /// <summary>Quality gate configurations applicable to this work item.</summary>
    public IReadOnlyList<QualityGateConfiguration>? QualityGateConfigs { get; init; }

    /// <summary>Reviewer configurations applicable to this work item.</summary>
    public IReadOnlyList<ReviewerConfiguration>? ReviewerConfigs { get; init; }

    /// <summary>MCP server configurations for the agent workspace.</summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }

    // --- Review-specific ---

    /// <summary>Linked pull request metadata for review runs.</summary>
    public LinkedPullRequest? LinkedPullRequest { get; init; }

    /// <summary>Target branch of the PR under review.</summary>
    public string? ReviewPrTargetBranch { get; init; }

    /// <summary>Description/body of the PR under review.</summary>
    public string? ReviewPrDescription { get; init; }

    /// <summary>Author of the PR under review.</summary>
    public string? ReviewPrAuthor { get; init; }

    // --- Decomposition-specific ---

    /// <summary>Project context for cross-repo decomposition.</summary>
    public DecompositionProjectContext? ProjectContext { get; init; }

    /// <summary>Source of decomposition request (e.g., epic issue URL).</summary>
    public string? DecompositionSource { get; init; }
}
