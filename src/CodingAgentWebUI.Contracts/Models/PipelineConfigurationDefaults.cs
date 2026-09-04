namespace CodingAgentWebUI.Pipeline.Models;

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

    /// <summary>Default review agents: Correctness + DotNetSpecialist + Security + TestQuality.</summary>
    public static IReadOnlyList<ReviewAgentConfig> DefaultReviewAgents { get; } = new[]
    {
        new ReviewAgentConfig { Name = "Correctness", Prompt = DefaultCorrectnessReviewPrompt },
        new ReviewAgentConfig { Name = "DotNetSpecialist", Prompt = DefaultDotNetSpecialistReviewPrompt },
        new ReviewAgentConfig { Name = "SecurityReviewer", Prompt = DefaultSecurityReviewPrompt },
        new ReviewAgentConfig { Name = "TestQualityReviewer", Prompt = DefaultTestQualityReviewPrompt }
    };

    /// <summary>
    /// Well-known ID for the default reviewer configuration.
    /// Used by the reset-to-defaults feature to identify/replace the factory configuration.
    /// </summary>
    public const string DefaultReviewerConfigurationId = "default-reviewers";

    /// <summary>
    /// Factory-default reviewer configurations. Used as the source of truth for
    /// "Reset collection to defaults" — replaces the entire reviewer config set.
    /// </summary>
    public static IReadOnlyList<ReviewerConfiguration> DefaultReviewerConfigurations { get; } = new[]
    {
        new ReviewerConfiguration
        {
            Id = DefaultReviewerConfigurationId,
            DisplayName = "Default Reviewers",
            MatchLabels = [],
            Agents = DefaultReviewAgents.Select(a => new ReviewAgent { Name = a.Name, Prompt = a.Prompt }).ToList(),
            Enabled = true,
            ExecutionOrder = 0
        }
    };

    public const string DefaultAnalysisPrompt = DefaultPrompts.Analysis;
    public const string DefaultAnalysisReviewPrompt = DefaultPrompts.AnalysisReview;
    public const string DefaultAnalysisRefinementPrompt = DefaultPrompts.AnalysisRefinement;
    public const string DefaultImplementationPrompt = DefaultPrompts.Implementation;
}
