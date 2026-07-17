// Feature: 029-pipeline-projects
// Task 11.2: Unit tests for ApplyProjectOverrides
// Validates: Requirements 3.2, 3.3, 3.7, 4.4
using System.Reflection;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="PipelineConfigurationResolver.ApplyProjectOverrides"/>.
/// Covers null → inherit, non-null → override, REPLACE semantics for nested objects,
/// and resolution order (Global → Project → ProviderConfig blacklist).
/// </summary>
// TODO: Add test for ArgumentOutOfRangeException handling via TargetInvocationException unwrapping
// and partial-apply semantics (properties applied before the failing one are retained in the clone).
// TODO: Add test validating that all [ProjectOverridable] Order values are unique — duplicate Order
// values produce non-deterministic iteration which breaks partial-apply-on-exception semantics.
// TODO: Add dedicated override tests for AcceptanceCriteriaEnabled, CiNotStartedTimeout,
// CiNotStartedMaxRetries, and AnalysisCommitThreshold — drift-detection verifies mapping exists
// but not that the override actually works at runtime for these properties.
// TODO: Add test asserting ApplyProjectOverrides does not mutate the original config object —
// the refactoring changed from immutable `with` expressions to clone-then-mutate-in-place.
public class ApplyProjectOverridesTests
{
    // ── Null project returns config unchanged ──────────────────────────────────

    [Fact]
    public void NullProject_ReturnsOriginalConfigReference()
    {
        var config = TestPipelineConfig.Default();

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, null);

        result.Should().BeSameAs(config);
    }

    // ── Null fields → inherit from global ──────────────────────────────────────

    [Fact]
    public void AllNullOverrides_LeavesConfigUnchanged()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.DefaultProject();

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

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
        result.BrainReadOnly.Should().Be(config.BrainReadOnly);
    }

    // ── Non-null fields → override global values ───────────────────────────────

    [Fact]
    public void MaxRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxRetries = 7 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(7);
    }

    [Fact]
    public void MaxAnalysisRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxAnalysisRetries = 4 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxAnalysisRetries.Should().Be(4);
    }

    [Fact]
    public void AgentTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AgentTimeout = TimeSpan.FromMinutes(60) };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
    }

    [Fact]
    public void AnalysisPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisPrompt = "Custom analysis prompt" };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AnalysisPrompt.Should().Be("Custom analysis prompt");
    }

    [Fact]
    public void ImplementationPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ImplementationPrompt = "Custom impl prompt" };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.ImplementationPrompt.Should().Be("Custom impl prompt");
    }

    [Fact]
    public void AnalysisReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { AnalysisReviewEnabled = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AnalysisReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void AnalysisReviewPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisReviewPrompt = "Project review prompt" };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AnalysisReviewPrompt.Should().Be("Project review prompt");
    }

    [Fact]
    public void AnalysisRefinementPrompt_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { AnalysisRefinementPrompt = "Refine it" };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AnalysisRefinementPrompt.Should().Be("Refine it");
    }

    [Fact]
    public void BaselineHealthCheckEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { BaselineHealthCheckEnabled = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.BaselineHealthCheckEnabled.Should().BeTrue();
    }

    [Fact]
    public void ExternalCiTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ExternalCiTimeout = TimeSpan.FromMinutes(45) };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.ExternalCiTimeout.Should().Be(TimeSpan.FromMinutes(45));
    }

    [Fact]
    public void ExternalCiPollInterval_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { ExternalCiPollInterval = TimeSpan.FromSeconds(60) };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.ExternalCiPollInterval.Should().Be(TimeSpan.FromSeconds(60));
    }

    [Fact]
    public void MaxInfrastructureRetries_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxInfrastructureRetries = 5 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxInfrastructureRetries.Should().Be(5);
    }

    [Fact]
    public void StallWarningInterval_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { StallWarningInterval = TimeSpan.FromMinutes(5) };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.StallWarningInterval.Should().Be(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void MaxDecompositionSubIssues_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxDecompositionSubIssues = 10 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxDecompositionSubIssues.Should().Be(10);
    }

    [Fact]
    public void MaxConcurrentDecompositions_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxConcurrentDecompositions = 4 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxConcurrentDecompositions.Should().Be(4);
    }

    [Fact]
    public void DecompositionTimeout_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { DecompositionTimeout = TimeSpan.FromMinutes(25) };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.DecompositionTimeout.Should().Be(TimeSpan.FromMinutes(25));
    }

    [Fact]
    public void MaxOpenIssuesForContext_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxOpenIssuesForContext = 75 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxOpenIssuesForContext.Should().Be(75);
    }

    [Fact]
    public void MaxRefactoringProposals_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { MaxRefactoringProposals = 8 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxRefactoringProposals.Should().Be(8);
    }

    [Fact]
    public void RefactoringReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { RefactoringReviewEnabled = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.RefactoringReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void BrainConsolidationReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { BrainConsolidationReviewEnabled = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.BrainConsolidationReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void HarnessSuggestionsReviewEnabled_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // false by default
        var project = TestPipelineConfig.WithProject() with { HarnessSuggestionsReviewEnabled = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.HarnessSuggestionsReviewEnabled.Should().BeTrue();
    }

    [Fact]
    public void BlacklistedPaths_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default(); // [".agent", ".github"]
        var projectPaths = new List<string> { "vendor", "dist", "coverage" };
        var project = TestPipelineConfig.WithProject() with { BlacklistedPaths = projectPaths };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);
    }

    [Fact]
    public void BrainReadOnly_NonNull_OverridesGlobal()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with { BrainReadOnly = true };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.BrainReadOnly.Should().BeTrue();
    }

    // ── CodeReview deep-merge semantics ──────────────────────────────────────────

    [Fact]
    public void CodeReview_FullOverride_AppliesAllValues()
    {
        // Global config has MaxIterations=2, FixPrompt=null
        var config = TestPipelineConfig.Default();
        var projectCodeReview = new CodeReviewOverrides
        {
            MaxIterations = 5,
            FixPrompt = "Custom fix prompt",
            ReviewIsolation = ReviewIsolation.Shared,
            InlineComments = new InlineCommentOverrides
            {
                Enabled = false,
                MaxInlineComments = 10,
                MaxRetries = 3,
                OrderBySeverity = false,
                SeverityThreshold = FindingSeverity.Critical,
            },
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // All specified override values are applied
        result.CodeReview.MaxIterations.Should().Be(5);
        result.CodeReview.FixPrompt.Should().Be("Custom fix prompt");
        result.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Shared);
        result.CodeReview.InlineComments.Enabled.Should().BeFalse();
        result.CodeReview.InlineComments.MaxInlineComments.Should().Be(10);
        result.CodeReview.InlineComments.MaxRetries.Should().Be(3);
        result.CodeReview.InlineComments.OrderBySeverity.Should().BeFalse();
        result.CodeReview.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void CodeReview_PartialOverride_PreservesUnspecifiedGlobalValues()
    {
        // Global has MaxIterations=2, FixPrompt=null, ReviewIsolation=Isolated, InlineComments defaults
        var config = TestPipelineConfig.Default() with
        {
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 2,
                FixPrompt = "Global fix prompt",
                ReviewIsolation = ReviewIsolation.Isolated,
                InlineComments = new InlineCommentSettings
                {
                    Enabled = true,
                    MaxInlineComments = 20,
                    MaxRetries = 2,
                    OrderBySeverity = true,
                    SeverityThreshold = FindingSeverity.Suggestion,
                },
            }
        };
        // Project sets only MaxIterations=1, everything else stays null (don't override)
        var projectCodeReview = new CodeReviewOverrides
        {
            MaxIterations = 1,
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // Deep-merge: only MaxIterations is overridden, everything else preserved from global
        result.CodeReview.MaxIterations.Should().Be(1);
        result.CodeReview.FixPrompt.Should().Be("Global fix prompt");
        result.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
        result.CodeReview.InlineComments.Enabled.Should().BeTrue();
        result.CodeReview.InlineComments.MaxInlineComments.Should().Be(20);
        result.CodeReview.InlineComments.MaxRetries.Should().Be(2);
        result.CodeReview.InlineComments.OrderBySeverity.Should().BeTrue();
        result.CodeReview.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Suggestion);
    }

    [Fact]
    public void CodeReview_InlineCommentsPartialOverride_PreservesUnspecifiedProperties()
    {
        var config = TestPipelineConfig.Default() with
        {
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 3,
                InlineComments = new InlineCommentSettings
                {
                    Enabled = true,
                    MaxInlineComments = 25,
                    MaxRetries = 4,
                    OrderBySeverity = false,
                    SeverityThreshold = FindingSeverity.Critical,
                },
            }
        };
        // Override only MaxInlineComments within InlineComments
        var projectCodeReview = new CodeReviewOverrides
        {
            InlineComments = new InlineCommentOverrides
            {
                MaxInlineComments = 5,
            },
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // MaxInlineComments overridden, everything else preserved
        result.CodeReview.MaxIterations.Should().Be(3);
        result.CodeReview.InlineComments.MaxInlineComments.Should().Be(5);
        result.CodeReview.InlineComments.Enabled.Should().BeTrue();
        result.CodeReview.InlineComments.MaxRetries.Should().Be(4);
        result.CodeReview.InlineComments.OrderBySeverity.Should().BeFalse();
        result.CodeReview.InlineComments.SeverityThreshold.Should().Be(FindingSeverity.Critical);
    }

    [Fact]
    public void CodeReview_NullInlineCommentsOverride_PreservesGlobalInlineComments()
    {
        var globalInlineComments = new InlineCommentSettings
        {
            Enabled = false,
            MaxInlineComments = 30,
            MaxRetries = 5,
        };
        var config = TestPipelineConfig.Default() with
        {
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 2,
                InlineComments = globalInlineComments,
            }
        };
        // Override MaxIterations but leave InlineComments null (don't override)
        var projectCodeReview = new CodeReviewOverrides
        {
            MaxIterations = 4,
            InlineComments = null,
        };
        var project = TestPipelineConfig.WithProject() with { CodeReview = projectCodeReview };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.CodeReview.MaxIterations.Should().Be(4);
        result.CodeReview.InlineComments.Should().Be(globalInlineComments);
    }

    [Fact]
    public void CodeReview_Null_InheritsGlobalObject()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject(); // CodeReview is null

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

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
        };

        // 3. ProviderConfig overrides the project-level blacklist (repo-specific)
        var providerConfig = new ProviderConfig
        {
            Id = "repo-1",
            DisplayName = "Repo 1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            BlacklistedPaths = ["secrets", "internal"],
        };

        // Apply in correct resolution order: Global → Project → ProviderConfig
        var afterProject = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfigurationResolver.ApplyBlacklistOverride(afterProject, providerConfig);

        // Project overrides should have applied over global
        afterProject.BlacklistedPaths.Should().BeEquivalentTo(projectPaths);

        // ProviderConfig should override the project-level values
        afterProvider.BlacklistedPaths.Should().BeEquivalentTo(new[] { "secrets", "internal" });
    }

    [Fact]
    public void ResolutionOrder_NullProviderConfig_PreservesProjectBlacklist()
    {
        var globalConfig = TestPipelineConfig.Default();
        var projectPaths = new List<string> { "vendor" };
        var project = TestPipelineConfig.WithProject() with { BlacklistedPaths = projectPaths };

        var afterProject = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfigurationResolver.ApplyBlacklistOverride(afterProject, null);

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
        };

        var afterProject = PipelineConfigurationResolver.ApplyProjectOverrides(globalConfig, project);
        var afterProvider = PipelineConfigurationResolver.ApplyBlacklistOverride(afterProject, providerConfig);

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

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

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

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

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

    // ── Drift-detection tests ──────────────────────────────────────────────────

    /// <summary>
    /// Well-known PipelineProject fields that are NOT behavioral overrides.
    /// These fields are structural/identity/metadata — never applied via ApplyProjectOverrides.
    /// </summary>
    private static readonly HashSet<string> NonOverrideProjectFields = new(StringComparer.Ordinal)
    {
        nameof(PipelineProject.Id),
        nameof(PipelineProject.Name),
        nameof(PipelineProject.Description),
        nameof(PipelineProject.Enabled),
        nameof(PipelineProject.TemplateIds),
        nameof(PipelineProject.EpicIssueProviderId),
        nameof(PipelineProject.SteeringContent),
        nameof(PipelineProject.Secrets),
    };

    [Fact]
    public void DriftDetection_AllProjectOverrideProperties_HaveMatchingProjectOverridableAttribute()
    {
        // Forward check: every nullable behavioral property on PipelineProject
        // must have a corresponding [ProjectOverridable]-annotated property on PipelineConfiguration.
        var configPropertiesWithAttribute = typeof(PipelineConfiguration)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<ProjectOverridableAttribute>() is not null)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var projectOverrideProperties = typeof(PipelineProject)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => !NonOverrideProjectFields.Contains(p.Name))
            .Select(p => p.Name)
            .ToList();

        var missingAnnotations = projectOverrideProperties
            .Where(name => !configPropertiesWithAttribute.Contains(name))
            .ToList();

        missingAnnotations.Should().BeEmpty(
            "every behavioral override property on PipelineProject should have a matching " +
            "[ProjectOverridable]-annotated property on PipelineConfiguration. " +
            $"Missing: [{string.Join(", ", missingAnnotations)}]");
    }

    [Fact]
    public void DriftDetection_AllProjectOverridableProperties_HaveMatchingProjectProperty()
    {
        // Reverse check: every [ProjectOverridable] property on PipelineConfiguration
        // must have a matching nullable property on PipelineProject.
        var projectPropertyNames = typeof(PipelineProject)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Select(p => p.Name)
            .ToHashSet(StringComparer.Ordinal);

        var annotatedConfigProperties = typeof(PipelineConfiguration)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<ProjectOverridableAttribute>() is not null)
            .Select(p => p.Name)
            .ToList();

        var missingProjectProperties = annotatedConfigProperties
            .Where(name => !projectPropertyNames.Contains(name))
            .ToList();

        missingProjectProperties.Should().BeEmpty(
            "every [ProjectOverridable]-annotated property on PipelineConfiguration should have a " +
            "matching property on PipelineProject. " +
            $"Missing: [{string.Join(", ", missingProjectProperties)}]");
    }

    [Fact]
    public void DriftDetection_NoInfrastructurePropertyIsAnnotatedAsOverridable()
    {
        // Guard: infrastructure fields must NOT be annotated with [ProjectOverridable]
        var annotatedConfigProperties = typeof(PipelineConfiguration)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<ProjectOverridableAttribute>() is not null)
            .Select(p => p.Name)
            .ToList();

        // These properties on PipelineProject are structural, not behavioral overrides
        var accidentallyAnnotated = annotatedConfigProperties
            .Where(name => NonOverrideProjectFields.Contains(name))
            .ToList();

        accidentallyAnnotated.Should().BeEmpty(
            "infrastructure/identity fields should NOT be annotated with [ProjectOverridable]. " +
            $"Incorrectly annotated: [{string.Join(", ", accidentallyAnnotated)}]");
    }

    [Fact]
    public void DriftDetection_DeepMergeProperties_OnlyCodeReviewCurrently()
    {
        // Guard: if this test fails, a second DeepMerge property has been added.
        // Before incrementing the expected count, verify:
        // 1. The new property is a simple auto-property (not delegating to a sub-config via get/init)
        // 2. If it delegates (like MaxRetries delegates to Retry.MaxRetries), the reflection clone
        //    in ApplyProjectOverrides calls GetValue AFTER SetValue on a shallow clone — but the
        //    shallow clone shares the sub-config reference, so SetValue on the flat property creates
        //    a NEW sub-config instance that the getter doesn't read from. This produces stale data.
        // 3. Test manually: set a project override for the new property's nested value and verify
        //    the merged result reflects the override (not the original global value).
        var deepMergeProperties = typeof(PipelineConfiguration)
            .GetProperties(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Where(p => p.GetCustomAttribute<ProjectOverridableAttribute>()?.DeepMerge == true)
            .ToList();

        deepMergeProperties.Should().ContainSingle(
            "Only CodeReview currently uses DeepMerge. If you're adding a second DeepMerge property, " +
            "verify it is a simple auto-property (not delegating to a sub-config). Delegating properties " +
            "cause GetValue after SetValue on a shallow clone to read stale data — see the TODO in " +
            "ApplyProjectOverrides. Test the override manually before changing this assertion.")
            .Which.Name.Should().Be(nameof(PipelineConfiguration.CodeReview));
    }
}
