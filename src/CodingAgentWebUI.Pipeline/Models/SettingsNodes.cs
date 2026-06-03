namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Constants for settings tree navigation node IDs.
/// Used by SettingsTreeNav and Settings.razor to avoid magic strings.
/// </summary>
public static class SettingsNodes
{
    public const string ProvidersIssue = "providers-issue";
    public const string ProvidersRepository = "providers-repository";
    public const string ProvidersAgent = "providers-agent";
    public const string ProvidersPipeline = "providers-pipeline";
    public const string PipelineGeneral = "pipeline-general";
    public const string PipelineLoop = "pipeline-loop";
    public const string PipelinePrompts = "pipeline-prompts";
    public const string PipelineImplementation = "pipeline-implementation";
    public const string PipelineReview = "pipeline-review";
    public const string PipelineDecomposition = "pipeline-decomposition";
    public const string PipelineConsolidation = "pipeline-consolidation";
    public const string AgentProfiles = "agent-profiles";
    public const string QualityGateConfigs = "quality-gate-configs";
    public const string ReviewerConfigs = "reviewer-configs";
    public const string Projects = "projects";
    public const string ProjectDetail = "project-detail";
}
