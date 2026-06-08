// Feature: 029-pipeline-projects
// Task 11.2: Unit tests for ApplyProjectOverrides
// Validates: Requirements 3.2, 3.3, 3.7, 4.4
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="PipelineConfiguration.ApplyProjectOverrides"/>.
/// Covers null → inherit, non-null → override, REPLACE semantics for nested objects,
/// and resolution order (Global → Project → ProviderConfig blacklist).
/// </summary>
public class ApplyProjectOverridesTests
{
    // ── Null project returns config unchanged ──────────────────────────────────

    [Fact]
    public void NullProject_ReturnsOriginalConfigReference()
    {
        var config = TestPipelineConfig.Default();

        var result = PipelineConfiguration.ApplyProjectOverrides(config, null);

        result.Should().BeSameAs(config);
    }

    // ── Null fields → inherit from global ──────────────────────────────────────

    [Fact]
    public void AllNullOverrides_LeavesConfigUnchanged()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.DefaultProject();

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(config.MaxRetries);
        result.MaxAnalysisRetries.Should().Be(config.MaxAnalysisRetries);
        result.AgentTimeout.Should().Be(config.AgentTimeout);
        result.AnalysisPrompt.Should().Be(config.AnalysisPrompt);
        result.ImplementationPrompt.Should().Be(config.ImplementationPrompt);
        result.AnalysisReviewEnabled.Should().Be(config.AnalysisReviewEnabled);
        result.AnalysisReviewPrompt.Should().Be(config.AnalysisReviewPrompt);
        result.AnalysisRefinementPrompt.Should().Be(config.AnalysisRefinementPrompt);
        result.CodeReview.Should().BeSameAs(config.CodeReview);
        result.BaselineHealthCheckEnabled.Should().Be(config.BaselineHealthCheckEnabled);
        result.ExternalCiTimeout.Should().Be(config.ExternalCiTimeout);
        result.ExternalCiPollInterval.Should().Be(config.ExternalCiPollInterval);
        result.MaxInfrastructureRetries.Should().Be(config.MaxInfrastructureRetries);
        result.StallWarningInterval.Should().Be(config.StallWarningInterval);
        result.MaxDecompositionSubIssues.Should().Be(config.MaxDecompositionSubIssues);
        result.MaxConcurrentDecompositions.Should().Be(config.MaxConcurrentDecompositions);
        result.DecompositionTimeout.Should().Be(config.DecompositionTimeout);
        result.MaxOpenIssuesForContext.Should().Be(config.MaxOpenIssuesForContext);
        result.MaxRefactoringProposals.Should().Be(config.MaxRefactoringProposals);
        result.RefactoringReviewEnabled.Should().Be(config.RefactoringReviewEnabled);
        result.BrainConsolidationReviewEnabled.Should().Be(config.BrainConsolidationReviewEnabled);
        result.HarnessSuggestionsReviewEnabled.Should().Be(config.HarnessSuggestionsReviewEnabled);
        result.BlacklistedPaths.Should().BeSameAs(config.BlacklistedPaths);
        result.BlacklistMode.Should().Be(config.BlacklistMode);
        result.BrainReadOnly.Should().Be(config.BrainReadOnly);
    }

    // ── Non-null fields → override global values ───────────────────────────────

    [Fact]
    public void MaxRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxRetries = 7 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(7);
    }

    [Fact]
    public void MaxAnalysisRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxAnalysisRetries = 4 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxAnalysisRetries.Should().Be(4);
    }

    [Fact]
    public void AgentTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AgentTimeout = TimeSpan.FromMinutes(60) };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void AnalysisPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisPrompt = "Custom analysis prompt" };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.AnalysisPrompt.Should().Be("Custom analysis prompt");
    }

    [Fact]
    public void ImplementationPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ImplementationPrompt = "Custom impl prompt" };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.ImplementationPrompt.Should().Be("Custom impl prompt");
    }

    [Fact]
    public void AnalysisReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { AnalysisReviewEnabled = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.AnalysisReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void AnalysisReviewPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisReviewPrompt = "Project review prompt" };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.AnalysisReviewPrompt.Should().Be("Project review prompt");
    }

    [Fact]
    public void AnalysisRefinementPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisRefinementPrompt = "Refine it" };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.AnalysisRefinementPrompt.Should().Be("Refine it");
    }

    [Fact]
    public void BaselineHealthCheckEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { BaselineHealthCheckEnabled = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.BaselineHealthCheckEnabled.Should().BeTrue();
    }

    [Fact]
    public void ExternalCiTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ExternalCiTimeout = TimeSpan.FromMinutes(45) };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.ExternalCiTimeout.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void ExternalCiPollInterval_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ExternalCiPollInterval = TimeSpan.FromSeconds(60) };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.ExternalCiPollInterval.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MaxInfrastructureRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxInfrastructureRetries = 5 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxInfrastructureRetries.Should().Be(5);
    }

    [Fact]
    public void StallWarningInterval_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { StallWarningInterval = TimeSpan.FromMinutes(5) };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.StallWarningInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void MaxDecompositionSubIssues_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxDecompositionSubIssues = 10 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxDecompositionSubIssues.Should().Be(10);
    }

    [Fact]
    public void MaxConcurrentDecompositions_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxConcurrentDecompositions = 4 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxConcurrentDecompositions.Should().Be(4);
    }

    [Fact]
    public void DecompositionTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { DecompositionTimeout = TimeSpan.FromMinutes(25) };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.DecompositionTimeout.Should().Be(TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void MaxOpenIssuesForContext_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxOpenIssuesForContext = 75 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxOpenIssuesForContext.Should().Be(75);
    }

    [Fact]
    public void MaxRefactoringProposals_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxRefactoringProposals = 8 };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxRefactoringProposals.Should().Be(8);
    }

    [Fact]
    public void RefactoringReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { RefactoringReviewEnabled = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.RefactoringReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void BrainConsolidationReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { BrainConsolidationReviewEnabled = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.BrainConsolidationReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void HarnessSuggestionsReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { HarnessSuggestionsReviewEnabled = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.HarnessSuggestionsReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void BlacklistedPaths_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // [".agent", ".github"]
        var projectPaths = new List<string> { "vendor", "dist", "coverage" };
        var project = TestPipelineConfig.WithProject() with { BlacklistedPaths = projectPaths };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);
    }

    [Fact]
    public void BlacklistMode_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // WarnAndExclude by default
        var project = TestPipelineConfig.WithProject() with { BlacklistMode = BlacklistMode.WarnAndExclude };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
    }

    [Fact]
    public void BrainReadOnly_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { BrainReadOnly = true };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.BrainReadOnly.Should().BeTrue();
    }

    // ── CodeReview REPLACE semantics ───────────────────────────────────────────

    [Fact]
    public void CodeReview_NonNull_ReplacesEntireObject()
    {
        // Global config has MaxIterations=2, FixPrompt=null
        var config = TestPipelineConfig.Default();
        var projectCodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 5,
            FixPrompt = "Custom fix prompt",
            ReviewIsolation = ReviewIsolation.Isolated,
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        // Entire object replaced — not deep merged
        result.CodeReview.Should().BeSameAs(projectCodeReview);
        result.CodeReview.MaxIterations.Should().Be(5);
        result.CodeReview.FixPrompt.Should().Be("Custom fix prompt");
        result.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
    }

    [Fact]
    public void CodeReview_NonNull_DoesNotDeepMergeWithGlobal()
    {
        // Global has MaxIterations=2, FixPrompt=null
        var config = TestPipelineConfig.Default();
        // Project sets only MaxIterations=1, FixPrompt stays null
        var projectCodeReview = new CodeReviewConfiguration
        {
            MaxIterations = 1,
            FixPrompt = null,
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        // REPLACE semantics: the project's CodeReview object replaces the global one entirely.
        // FixPrompt is null because the project object has it as null — it does NOT inherit
        // from the global CodeReview.FixPrompt (which also happened to be null here).
        result.CodeReview.Should().BeSameAs(projectCodeReview);
        result.CodeReview.MaxIterations.Should().Be(1);
    }

    [Fact]
    public void CodeReview_Null_InheritsGlobalObject()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject(); // CodeReview is null

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.CodeReview.Should().BeSameAs(config.CodeReview);
    }

    // ── Resolution order: Global → Project → ProviderConfig blacklist ──────────

    [Fact]
    public void ResolutionOrder_ProjectOverridesGlobal_ThenProviderConfigOverridesProject()
    {
        // 1. Start with global config
        var globalConfig = TestPipelineConfig.Default(); // BlacklistedPaths = [".agent", ".github"]

        // 2. Project overrides the blacklist
        var projectPaths = new List<string> { "vendor", "node_modules" };
        var project = TestPipelineConfig.WithProject() with
        {
            BlacklistedPaths = projectPaths,
            BlacklistMode = BlacklistMode.WarnAndExclude,
        };

        // 3. ProviderConfig overrides the project-level blacklist (repo-specific)
        var providerConfig = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo 1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            BlacklistedPaths = ["secrets", "internal"],
            BlacklistMode = BlacklistMode.WarnAndExclude,
        };

        // Apply in correct resolution order: Global → Project → ProviderConfig
        var afterProject = PipelineConfiguration.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfiguration.ApplyBlacklistOverride(afterProject, providerConfig);

        // Project overrides should have applied over global
        afterProject.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);
        afterProject.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);

        // ProviderConfig should override the project-level values
        afterProvider.BlacklistedPaths.Should().BeEquivalentTo(new[] { "secrets", "internal" });
        afterProvider.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
    }

    [Fact]
    public void ResolutionOrder_NullProviderConfig_PreservesProjectBlacklist()
    {
        var globalConfig = TestPipelineConfig.Default();
        var projectPaths = new List<string> { "vendor" };
        var project = TestPipelineConfig.WithProject() with { BlacklistedPaths = projectPaths };

        var afterProject = PipelineConfiguration.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfiguration.ApplyBlacklistOverride(afterProject, null);

        // No provider override → project-level blacklist preserved
        afterProvider.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);
    }

    [Fact]
    public void ResolutionOrder_EmptyProviderBlacklist_PreservesProjectBlacklist()
    {
        var globalConfig = TestPipelineConfig.Default();
        var projectPaths = new List<string> { "vendor", "build" };
        var project = TestPipelineConfig.WithProject() with { BlacklistedPaths = projectPaths };
        var providerConfig = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo 1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            BlacklistedPaths = [], // empty — does NOT override
            BlacklistMode = null,
        };

        var afterProject = PipelineConfiguration.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfiguration.ApplyBlacklistOverride(afterProject, providerConfig);

        // Empty blacklist list does not override (Count > 0 check)
        afterProvider.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);
    }

    // ── Multiple overrides at once ─────────────────────────────────────────────

    [Fact]
    public void MultipleNonNullOverrides_AllApplied()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with
        {
            MaxRetries = 10,
            AgentTimeout = TimeSpan.FromMinutes(90),
            AnalysisReviewEnabled = true,
            MaxDecompositionSubIssues = 15,
            BrainReadOnly = true,
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(10);
        result.AgentTimeout.Should().Be(TimeSpan.FromMinutes(90));
        result.AnalysisReviewEnabled.Should().BeTrue();
        result.MaxDecompositionSubIssues.Should().Be(15);
        result.BrainReadOnly.Should().BeTrue();
        // Non-overridden fields remain at global defaults
        result.MaxAnalysisRetries.Should().Be(config.MaxAnalysisRetries);
        result.ExternalCiTimeout.Should().Be(config.ExternalCiTimeout);
    }

    // ── Infrastructure fields unaffected ───────────────────────────────────────

    [Fact]
    public void InfrastructureFields_NeverAffectedByProjectOverrides()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with
        {
            MaxRetries = 99,
            AgentTimeout = TimeSpan.FromHours(2),
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(config, project);

        // Infrastructure-level settings remain unchanged
        result.WorkspaceBaseDirectory.Should().Be(config.WorkspaceBaseDirectory);
        result.ClosedLoopPollInterval.Should().Be(config.ClosedLoopPollInterval);
        result.ClosedLoopMaxRunsPerCycle.Should().Be(config.ClosedLoopMaxRunsPerCycle);
        result.ClosedLoopMaxConsecutivePollFailures.Should().Be(config.ClosedLoopMaxConsecutivePollFailures);
        result.ClosedLoopMaxBackoffInterval.Should().Be(config.ClosedLoopMaxBackoffInterval);
        result.ClosedLoopMaxPagesToFetch.Should().Be(config.ClosedLoopMaxPagesToFetch);
        result.IssuePageSize.Should().Be(config.IssuePageSize);
        result.FailedWorkspaceRetentionDays.Should().Be(config.FailedWorkspaceRetentionDays);
    }
}
