// Feature: 029-pipeline-projects
// Property 5: Backward Compatibility
// Verify a freshly-migrated Default project with all null overrides produces identical config to pre-project behavior.
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Property-based tests for Backward Compatibility.
/// A Default project with all null overrides applied via ApplyProjectOverrides
/// produces the exact same PipelineConfiguration — no changes when all overrides are null.
/// This tests the fundamental backward compatibility guarantee: after migration,
/// the Default project with no overrides produces identical pipeline behavior.
/// **Validates: Requirements 2.2, 11.4**
/// </summary>
public class BackwardCompatibilityPropertyTests
{
    /// <summary>
    /// Property 5: Backward Compatibility — ApplyProjectOverrides with all-null overrides is identity.
    /// For any valid PipelineConfiguration, applying a Default project (WellKnownIds.DefaultProjectId)
    /// with ALL behavioral override fields set to null SHALL produce a result identical to the input.
    /// **Validates: Requirements 2.2, 11.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineConfigArbitraries) })]
    public void ApplyProjectOverrides_DefaultProjectWithNullOverrides_ProducesIdenticalConfig(
        PipelineConfiguration originalConfig)
    {
        // Arrange: create a Default project with ALL overrides null (simulates freshly-migrated state)
        var defaultProject = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            Enabled = true,
            TemplateIds = [],
            // All behavioral overrides are null by default (not set)
        };

        // Act: apply the Default project overrides to the config
        var result = PipelineConfigurationResolver.ApplyProjectOverrides(originalConfig, defaultProject);

        // Assert: result must be identical to original — no field changed
        Assert.Equal(originalConfig.MaxRetries, result.MaxRetries);
        Assert.Equal(originalConfig.MaxAnalysisRetries, result.MaxAnalysisRetries);
        Assert.Equal(originalConfig.AgentTimeout, result.AgentTimeout);
        Assert.Equal(originalConfig.AnalysisPrompt, result.AnalysisPrompt);
        Assert.Equal(originalConfig.ImplementationPrompt, result.ImplementationPrompt);
        Assert.Equal(originalConfig.AnalysisReviewEnabled, result.AnalysisReviewEnabled);
        Assert.Equal(originalConfig.AnalysisReviewPrompt, result.AnalysisReviewPrompt);
        Assert.Equal(originalConfig.AnalysisRefinementPrompt, result.AnalysisRefinementPrompt);
        Assert.Equal(originalConfig.CodeReview.MaxIterations, result.CodeReview.MaxIterations);
        Assert.Equal(originalConfig.CodeReview.FixPrompt, result.CodeReview.FixPrompt);
        Assert.Equal(originalConfig.BaselineHealthCheckEnabled, result.BaselineHealthCheckEnabled);
        Assert.Equal(originalConfig.ExternalCiTimeout, result.ExternalCiTimeout);
        Assert.Equal(originalConfig.ExternalCiPollInterval, result.ExternalCiPollInterval);
        Assert.Equal(originalConfig.MaxInfrastructureRetries, result.MaxInfrastructureRetries);
        Assert.Equal(originalConfig.StallWarningInterval, result.StallWarningInterval);
        Assert.Equal(originalConfig.MaxDecompositionSubIssues, result.MaxDecompositionSubIssues);
        Assert.Equal(originalConfig.MaxConcurrentDecompositions, result.MaxConcurrentDecompositions);
        Assert.Equal(originalConfig.DecompositionTimeout, result.DecompositionTimeout);
        Assert.Equal(originalConfig.MaxOpenIssuesForContext, result.MaxOpenIssuesForContext);
        Assert.Equal(originalConfig.MaxRefactoringProposals, result.MaxRefactoringProposals);
        Assert.Equal(originalConfig.RefactoringReviewEnabled, result.RefactoringReviewEnabled);
        Assert.Equal(originalConfig.BrainConsolidationReviewEnabled, result.BrainConsolidationReviewEnabled);
        Assert.Equal(originalConfig.HarnessSuggestionsReviewEnabled, result.HarnessSuggestionsReviewEnabled);
        Assert.Equal(originalConfig.BlacklistedPaths, result.BlacklistedPaths);
        Assert.Equal(originalConfig.BrainReadOnly, result.BrainReadOnly);

        // Also verify infrastructure-level settings remain untouched
        Assert.Equal(originalConfig.WorkspaceBaseDirectory, result.WorkspaceBaseDirectory);
        Assert.Equal(originalConfig.ClosedLoopPollInterval, result.ClosedLoopPollInterval);
        Assert.Equal(originalConfig.ClosedLoopMaxRunsPerCycle, result.ClosedLoopMaxRunsPerCycle);
        Assert.Equal(originalConfig.IssuePageSize, result.IssuePageSize);
    }

    /// <summary>
    /// Property 5b: ApplyProjectOverrides with null project is also identity.
    /// When no project is provided (null), the configuration is returned unchanged.
    /// **Validates: Requirements 2.2, 11.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(PipelineConfigArbitraries) })]
    public void ApplyProjectOverrides_NullProject_ReturnsUnchangedConfig(
        PipelineConfiguration originalConfig)
    {
        // Act
        var result = PipelineConfigurationResolver.ApplyProjectOverrides(originalConfig, null);

        // Assert: exact same reference returned (no allocation)
        Assert.Same(originalConfig, result);
    }
}

/// <summary>
/// FsCheck arbitrary generators for PipelineConfiguration instances.
/// Generates random configs with varying behavioral settings to ensure
/// the backward compatibility property holds across the full input space.
/// </summary>
public class PipelineConfigArbitraries
{
    private static readonly string[] PromptPool =
    [
        "Analyze the codebase",
        "Implement the feature",
        "Review the analysis carefully",
        "Refine based on feedback",
        "Custom prompt for testing",
        "Short"
    ];

    private static readonly string[] PathPool =
        [".agent", ".github", "node_modules", "bin", "obj", ".vs", "dist"];

    private static readonly string[] WorkspaceDirPool =
        ["/tmp/workspaces", "C:\\Temp\\Workspaces", "/var/pipeline/ws"];

    public static Arbitrary<PipelineConfiguration> PipelineConfigurationArb()
    {
        var gen =
            from maxRetries in Gen.Choose(1, 10)
            from maxAnalysisRetries in Gen.Choose(0, 5)
            from agentTimeoutMin in Gen.Choose(5, 120)
            from analysisPrompt in Gen.Elements(PromptPool)
            from implementationPrompt in Gen.Elements(PromptPool)
            from analysisReviewEnabled in Gen.Elements(true, false)
            from analysisReviewPrompt in Gen.Elements(PromptPool)
            from analysisRefinementPrompt in Gen.Elements(PromptPool)
            from codeReviewMaxIter in Gen.Choose(1, 5)
            from baselineHealthCheck in Gen.Elements(true, false)
            from externalCiTimeoutMin in Gen.Choose(5, 60)
            from externalCiPollSec in Gen.Choose(10, 120)
            from maxInfraRetries in Gen.Choose(1, 5)
            from stallWarnMin in Gen.Choose(1, 10)
            from maxDecompSubIssues in Gen.Choose(1, 20)
            from maxConcurrentDecomp in Gen.Choose(1, 5)
            from decompTimeoutMin in Gen.Choose(5, 30)
            from maxOpenIssues in Gen.Choose(10, 100)
            from maxRefactoring in Gen.Choose(1, 10)
            from refactoringReview in Gen.Elements(true, false)
            from brainConsolidationReview in Gen.Elements(true, false)
            from harnessSuggestionsReview in Gen.Elements(true, false)
            from blacklistCount in Gen.Choose(1, 4)
            from blacklistedPaths in Gen.ArrayOf(Gen.Elements(PathPool)).Resize(blacklistCount)
            from brainReadOnly in Gen.Elements(true, false)
            from workspaceDir in Gen.Elements(WorkspaceDirPool)
            select new PipelineConfiguration
            {
                MaxRetries = maxRetries,
                MaxAnalysisRetries = maxAnalysisRetries,
                AgentTimeout = TimeSpan.FromMinutes(agentTimeoutMin),
                AnalysisPrompt = analysisPrompt,
                ImplementationPrompt = implementationPrompt,
                AnalysisReviewEnabled = analysisReviewEnabled,
                AnalysisReviewPrompt = analysisReviewPrompt,
                AnalysisRefinementPrompt = analysisRefinementPrompt,
                CodeReview = new CodeReviewConfiguration
                {
                    MaxIterations = codeReviewMaxIter,
                    FixPrompt = null
                },
                BaselineHealthCheckEnabled = baselineHealthCheck,
                ExternalCiTimeout = TimeSpan.FromMinutes(externalCiTimeoutMin),
                ExternalCiPollInterval = TimeSpan.FromSeconds(externalCiPollSec),
                MaxInfrastructureRetries = maxInfraRetries,
                StallWarningInterval = TimeSpan.FromMinutes(stallWarnMin),
                MaxDecompositionSubIssues = maxDecompSubIssues,
                MaxConcurrentDecompositions = maxConcurrentDecomp,
                DecompositionTimeout = TimeSpan.FromMinutes(decompTimeoutMin),
                MaxOpenIssuesForContext = maxOpenIssues,
                MaxRefactoringProposals = maxRefactoring,
                RefactoringReviewEnabled = refactoringReview,
                BrainConsolidationReviewEnabled = brainConsolidationReview,
                HarnessSuggestionsReviewEnabled = harnessSuggestionsReview,
                BlacklistedPaths = blacklistedPaths.Distinct().ToList(),
                BrainReadOnly = brainReadOnly,
                WorkspaceBaseDirectory = workspaceDir,
            };

        return gen.ToArbitrary();
    }
}
