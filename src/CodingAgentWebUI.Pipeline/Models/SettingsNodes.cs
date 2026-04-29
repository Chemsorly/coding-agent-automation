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
    public const string PipelineQuality = "pipeline-quality";
    public const string PipelineSecurity = "pipeline-security";
    public const string AgentProfiles = "agent-profiles";
    public const string QualityGateConfigs = "quality-gate-configs";
}
