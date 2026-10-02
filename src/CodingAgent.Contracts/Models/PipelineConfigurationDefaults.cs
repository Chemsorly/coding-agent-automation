namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Factory defaults for review agents, reviewer configurations, and prompt templates.
/// Extracted from <see cref="PipelineConfiguration"/> to keep the data record focused on properties.
/// </summary>
public static class PipelineConfigurationDefaults
{
    public const string DefaultFixPrompt = DefaultPrompts.Fix;
    public const string DefaultCorrectnessReviewPrompt = DefaultPrompts.CorrectnessReview;
    public const string DefaultDotNetSpecialistReviewPrompt = DefaultPrompts.DotNetSpecialistReview;
    public const string DefaultSecurityReviewPrompt = DefaultPrompts.SecurityReview;
    public const string DefaultTestQualityReviewPrompt = DefaultPrompts.TestQualityReview;
    public const string DefaultAcceptanceCriteriaReviewPrompt = DefaultPrompts.AcceptanceCriteriaReview;

    /// <summary>Default review agents for every repository, whatever its stack: Correctness + Security + TestQuality.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "Correctness", Prompt = DefaultCorrectnessReviewPrompt },
        new ReviewAgentConfig { Name = "SecurityReviewer", Prompt = DefaultSecurityReviewPrompt },
        new ReviewAgentConfig { Name = "TestQualityReviewer", Prompt = DefaultTestQualityReviewPrompt }
    };

    /// <summary>Default review agents for .NET repositories (label <c>dotnet</c>): DotNetSpecialist.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultDotNetReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = DefaultDotNetSpecialistReviewPrompt }
    };

    /// <summary>
    /// Well-known ID for the default reviewer configuration.
    /// Used by the reset-to-defaults feature to identify/replace the factory configuration.
    /// </summary>
    public const string DefaultReviewerConfigurationId = "default-reviewers";

    /// <summary>Well-known ID for the default .NET reviewer configuration.</summary>
    public const string DefaultDotNetReviewerConfigurationId = "dotnet-reviewers";

    /// <summary>
    /// Factory-default reviewer configurations: the stack-agnostic reviewers for every repository, and the
    /// .NET specialist for repositories labelled <c>dotnet</c>, as stack reviewers go with the stack's quality gates.
    /// Used as the source of truth for "Reset collection to defaults" — replaces the entire reviewer config set.
    /// </summary>
    public static IReadOnlyList<ReviewerConfiguration> DefaultReviewerConfigurations { get; } = new[]
    {
        new ReviewerConfiguration
        {
            Id = DefaultReviewerConfigurationId,
            DisplayName = "Default Reviewers",
            MatchLabels = [],
            Agents = ToReviewAgents(DefaultReviewAgents),
            Enabled = true,
            ExecutionOrder = 0
        },
        new ReviewerConfiguration
        {
            Id = DefaultDotNetReviewerConfigurationId,
            DisplayName = ".NET Reviewers",
            MatchLabels = ["dotnet"],
            Agents = ToReviewAgents(DefaultDotNetReviewAgents),
            Enabled = true,
            ExecutionOrder = 1
        }
    };

    private static List<ReviewAgent> ToReviewAgents(IEnumerable<ReviewAgentConfig> agents) =>
        agents.Select(a => new ReviewAgent { Name = a.Name, Prompt = a.Prompt }).ToList();

    public const string DefaultAnalysisPrompt = DefaultPrompts.Analysis;
    public const string DefaultAnalysisReviewPrompt = DefaultPrompts.AnalysisReview;
    public const string DefaultAnalysisRefinementPrompt = DefaultPrompts.AnalysisRefinement;
    public const string DefaultImplementationPrompt = DefaultPrompts.Implementation;

    /// <summary>The instructions of a project reviewer whose prompt is empty (see <see cref="PipelineProject.ProjectReviewers"/>).</summary>
    public const string DefaultProjectReviewPrompt = DefaultPrompts.ProjectReview;

    /// <summary>The name of a project reviewer whose name is empty.</summary>
    public const string DefaultProjectReviewerName = "ProjectReviewer";
}
