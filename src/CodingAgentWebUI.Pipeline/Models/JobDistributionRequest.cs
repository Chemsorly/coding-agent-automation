namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Serialized to WorkItems.Payload (JSONB). Contains the full context needed
/// by an agent to execute a work item. Does NOT contain per-project runtime secrets.
/// </summary>
public record JobDistributionRequest
{
    /// <summary>Issue identifier (e.g., "owner/repo#123").</summary>
    public required IssueIdentifier IssueIdentifier { get; init; }

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
    public Guid? ProjectId { get; init; }

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

    /// <summary>Whether existing analysis should be force-refreshed (e.g., after gate rejection).</summary>
    public bool ForceRefreshAnalysis { get; init; }

    /// <summary>Which staleness signal triggered force-refresh (null if not staleness-triggered).</summary>
    public string? StalenessSignal { get; init; }

    /// <summary>Number of prior analysis refreshes for this issue (for OTel).</summary>
    public int AnalysisRefreshCount { get; init; }

    // --- Provider configs (serialized without secrets) ---

    /// <summary>Provider configurations relevant to this work item.</summary>
    public IReadOnlyList<ProviderConfig>? ProviderConfigs { get; init; }

    /// <summary>Pipeline configuration snapshot at enqueue time.</summary>
    public PipelineConfiguration? PipelineConfiguration { get; init; }

    /// <summary>Resolved agent profile ID for this work item.</summary>
    public string? ResolvedProfileId { get; init; }

    /// <summary>Resolved agent provider config ID from the agent profile.</summary>
    public string? AgentProviderConfigId { get; init; }

    /// <summary>Quality gate configurations applicable to this work item.</summary>
    public IReadOnlyList<QualityGateConfiguration>? QualityGateConfigs { get; init; }

    /// <summary>Reviewer configurations applicable to this work item.</summary>
    public IReadOnlyList<ReviewerConfiguration>? ReviewerConfigs { get; init; }

    /// <summary>MCP server configurations for the agent workspace.</summary>
    public IReadOnlyList<McpServerConfig>? McpServers { get; init; }

    /// <summary>Project-level steering content (markdown) for agent workspace.</summary>
    public string? ProjectSteeringContent { get; init; }

    /// <summary>Repository-level steering content (markdown) for agent workspace.</summary>
    public string? RepoSteeringContent { get; init; }

    /// <summary>W3C trace context (traceparent, tracestate) for distributed tracing.</summary>
    public Dictionary<string, string>? TraceContext { get; init; }

    /// <summary>Pre-fetched linked issue contexts for PR review runs.</summary>
    public IReadOnlyList<LinkedIssueContext>? LinkedIssueContexts { get; init; }

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

    // --- Consolidation-specific ---

    /// <summary>The consolidation run type (Brain, RefactoringDetection, HarnessSuggestions).</summary>
    public ConsolidationRunType? ConsolidationRunType { get; init; }

    /// <summary>Template ID for template-scoped consolidation runs (null for global).</summary>
    public string? ConsolidationTemplateId { get; init; }

    /// <summary>Workspace path for the consolidation run.</summary>
    public string? ConsolidationWorkspacePath { get; init; }

    /// <summary>
    /// When true, created refactoring issues will receive both <c>agent:generated</c> and
    /// <c>agent:next</c> labels. Propagated through the queue/drain/K8s path to ensure
    /// the flag reaches the executor even when the job is enqueued and dispatched later.
    /// Defaults to <c>false</c> for backward compatibility.
    /// </summary>
    public bool AutoDispatch { get; init; }

    /// <summary>
    /// Pre-assigned run/job ID from orchestration. When set, the work distributor
    /// should use this as the WorkItem ID instead of generating a new one.
    /// This ensures the agent's jobId matches the in-memory PipelineRun.RunId
    /// for hub method routing (token refresh, step transitions, etc.).
    /// </summary>
    public string? RunId { get; init; }

    // --- Factory methods ---

    /// <summary>
    /// Creates a <see cref="JobDistributionRequest"/> for an implementation dispatch.
    /// </summary>
    // Note: Unlike the old loop code which hardcoded IssueDetail.Description = "" and Labels = [],
    // this factory populates them with actual data from the issue summary when present.
    public static JobDistributionRequest FromTemplate(
        PipelineJobTemplate template,
        IssueSummary issue,
        string initiatedBy,
        int timeoutSeconds = 0,
        Guid? projectId = null,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(issue);

        return CreateBase(new JobDistributionRequestBaseParams(
            template, issue.Identifier, initiatedBy,
            WorkItemTaskType.Implementation, timeoutSeconds,
            PipelineRunType.Implementation, projectId, projectName))
        with
        {
            IssueDetail = new IssueDetail
            {
                Identifier = issue.Identifier,
                Title = issue.Title ?? "",
                Description = issue.Description ?? "",
                Labels = issue.Labels ?? []
            }
        };
    }

    /// <summary>
    /// Creates a <see cref="JobDistributionRequest"/> for a PR review dispatch.
    /// </summary>
    /// <param name="template">The pipeline job template providing provider config IDs.</param>
    /// <param name="pr">The pull request to review.</param>
    /// <param name="initiatedBy">Identity that initiated this work item (e.g., "loop", "manual").</param>
    /// <param name="timeoutSeconds">Maximum execution time in seconds.</param>
    /// <param name="useFullPrMetadata">
    /// When <c>true</c> (default, manual dispatch), uses actual PR metadata (IsDraft, Number, Description).
    /// When <c>false</c> (loop dispatch), uses minimal hardcoded defaults.
    /// </param>
    /// <param name="projectId">Optional project ID.</param>
    /// <param name="projectName">Optional project display name.</param>
    public static JobDistributionRequest FromTemplate(
        PipelineJobTemplate template,
        PullRequestSummary pr,
        string initiatedBy,
        int timeoutSeconds = 0,
        bool useFullPrMetadata = true,
        Guid? projectId = null,
        string? projectName = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(pr);

        return CreateBase(new JobDistributionRequestBaseParams(
            template, pr.Identifier, initiatedBy,
            WorkItemTaskType.Review, timeoutSeconds,
            PipelineRunType.Review, projectId, projectName))
        with
        {
            IssueDetail = new IssueDetail
            {
                Identifier = pr.Identifier,
                Title = pr.Title ?? "",
                Description = useFullPrMetadata ? (pr.Description ?? "") : "",
                Labels = []
            },
            LinkedPullRequest = new LinkedPullRequest
            {
                Url = pr.Url,
                BranchName = pr.BranchName,
                IsDraft = useFullPrMetadata && pr.IsDraft,
                Number = useFullPrMetadata ? pr.Number : 0
            },
            ReviewPrTargetBranch = pr.TargetBranch,
            ReviewPrDescription = pr.Description,
            ReviewPrAuthor = pr.Author
        };
    }

    /// <summary>
    /// Creates a <see cref="JobDistributionRequest"/> for a decomposition dispatch.
    /// </summary>
    // Note: Unlike the old loop decomposition code which hardcoded IssueDetail.Description = "" and
    // Labels = [], this factory populates them with actual data from the issue summary when present.
    public static JobDistributionRequest FromTemplate(
        PipelineJobTemplate template,
        IssueSummary issue,
        PipelineRunType phase,
        string initiatedBy,
        string runId,
        int timeoutSeconds = 0,
        Guid? projectId = null,
        string? projectName = null,
        string? decompositionSource = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentException.ThrowIfNullOrEmpty(runId);

        return CreateBase(new JobDistributionRequestBaseParams(
            template, issue.Identifier, initiatedBy,
            WorkItemTaskType.Decomposition, timeoutSeconds,
            phase, projectId, projectName))
        with
        {
            DecompositionSource = decompositionSource,
            RunId = runId,
            IssueDetail = new IssueDetail
            {
                Identifier = issue.Identifier,
                Title = issue.Title ?? "",
                Description = issue.Description ?? "",
                Labels = issue.Labels ?? []
            }
        };
    }

    // Note: Unlike the old loop-dispatched review/decomposition code which did NOT set
    // PipelineProviderConfigId (it defaulted to null), this method always sets it from template.PipelineProviderId.
    private static JobDistributionRequest CreateBase(JobDistributionRequestBaseParams p)
    {
        return new JobDistributionRequest
        {
            IssueIdentifier = p.IssueIdentifier,
            IssueProviderConfigId = p.Template.IssueProviderId,
            RepoProviderConfigId = p.Template.RepoProviderId,
            BrainProviderConfigId = p.Template.BrainProviderId,
            PipelineProviderConfigId = p.Template.PipelineProviderId,
            InitiatedBy = p.InitiatedBy,
            TaskType = p.TaskType,
            AgentSelector = "",
            TimeoutSeconds = p.TimeoutSeconds,
            RunType = p.RunType,
            ProjectId = p.ProjectId,
            ProjectName = p.ProjectName
        };
    }
}

/// <summary>
/// Groups the 8 private parameters of <see cref="JobDistributionRequest"/>'s internal
/// <c>CreateBase</c> factory helper to satisfy S107.
/// </summary>
internal sealed record JobDistributionRequestBaseParams(
    PipelineJobTemplate Template,
    string IssueIdentifier,
    string InitiatedBy,
    WorkItemTaskType TaskType,
    int TimeoutSeconds,
    PipelineRunType RunType,
    Guid? ProjectId,
    string? ProjectName);
