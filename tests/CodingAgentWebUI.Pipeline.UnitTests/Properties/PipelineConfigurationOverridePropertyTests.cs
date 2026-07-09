using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.TestUtilities;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for PipelineConfiguration.ApplyProjectOverrides.
/// Validates:
///   1. Idempotence: applying the same override twice yields the same result.
///   2. Null-transparency: null fields don't change the config (inherit semantics).
///   3. Override dominance: non-null project field always wins over base config.
///   4. Crash-freedom: arbitrary override values never throw (caught by the guard clause).
/// </summary>
public class PipelineConfigurationOverridePropertyTests
{
    /// <summary>
    /// Idempotence: applying the same project override twice produces the same result
    /// as applying it once. ApplyProjectOverrides(ApplyProjectOverrides(base, P), P) == ApplyProjectOverrides(base, P)
    /// </summary>
    [Property(MaxTest = 20)]
    public bool ApplyProjectOverrides_IsIdempotent(
        PositiveInt maxRetries,
        bool analysisReviewEnabled,
        bool hasCodeReview)
    {
        var baseConfig = TestPipelineConfig.Default();
        var project = CreateOverrideProject(maxRetries.Get, analysisReviewEnabled, hasCodeReview);

        var once = PipelineConfiguration.ApplyProjectOverrides(baseConfig, project);
        var twice = PipelineConfiguration.ApplyProjectOverrides(once, project);

        return once.MaxRetries == twice.MaxRetries
            && once.AnalysisReviewEnabled == twice.AnalysisReviewEnabled
            && once.AgentTimeout == twice.AgentTimeout
            && (ReferenceEquals(once.CodeReview, twice.CodeReview)
                || once.CodeReview?.MaxIterations == twice.CodeReview?.MaxIterations);
    }

    /// <summary>
    /// Null-transparency: a project with all-null overrides produces a config
    /// identical to the input (nothing changes).
    /// </summary>
    [Property(MaxTest = 20)]
    public bool NullOverrides_PreserveAllConfigValues(PositiveInt maxRetries)
    {
        var baseConfig = TestPipelineConfig.Default() with { MaxRetries = maxRetries.Get };
        var nullProject = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "NullOverrideProject"
            // All override fields remain null → inherit
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(baseConfig, nullProject);

        return result.MaxRetries == baseConfig.MaxRetries
            && result.MaxAnalysisRetries == baseConfig.MaxAnalysisRetries
            && result.AgentTimeout == baseConfig.AgentTimeout
            && result.AnalysisReviewEnabled == baseConfig.AnalysisReviewEnabled
            && result.MaxDecompositionSubIssues == baseConfig.MaxDecompositionSubIssues
            && result.MaxConcurrentDecompositions == baseConfig.MaxConcurrentDecompositions
            && result.ExternalCiTimeout == baseConfig.ExternalCiTimeout;
    }

    /// <summary>
    /// Override dominance: when a project specifies a non-null value, it ALWAYS
    /// appears in the result regardless of the base config value.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool NonNullOverride_AlwaysDominates(PositiveInt baseRetries, PositiveInt overrideRetries)
    {
        var baseConfig = TestPipelineConfig.Default() with { MaxRetries = baseRetries.Get };
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Override",
            MaxRetries = overrideRetries.Get
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(baseConfig, project);

        return result.MaxRetries == overrideRetries.Get;
    }

    /// <summary>
    /// Sequential composition: applying two non-overlapping overrides in sequence
    /// produces a config that has both overrides applied (no interference).
    /// ApplyProjectOverrides(ApplyProjectOverrides(base, A), B) has both A's and B's values.
    /// Uses values within valid ranges to avoid ArgumentOutOfRangeException fallback.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool SequentialNonOverlappingOverrides_BothApplied(
        PositiveInt retriesRaw,
        PositiveInt subIssuesRaw)
    {
        // Constrain to valid ranges: MaxRetries has no upper bound documented,
        // MaxDecompositionSubIssues must be in [1, 20]
        var retriesOverride = Math.Max(1, retriesRaw.Get % 10 + 1); // 1-10
        var subIssuesOverride = Math.Max(1, subIssuesRaw.Get % 20 + 1); // 1-20

        var baseConfig = TestPipelineConfig.Default();

        var projectA = new PipelineProject
        {
            Id = "proj-a",
            Name = "A",
            MaxRetries = retriesOverride
            // MaxDecompositionSubIssues = null → doesn't touch it
        };

        var projectB = new PipelineProject
        {
            Id = "proj-b",
            Name = "B",
            MaxDecompositionSubIssues = subIssuesOverride
            // MaxRetries = null → doesn't touch it
        };

        var afterA = PipelineConfiguration.ApplyProjectOverrides(baseConfig, projectA);
        var afterBoth = PipelineConfiguration.ApplyProjectOverrides(afterA, projectB);

        return afterBoth.MaxRetries == retriesOverride
            && afterBoth.MaxDecompositionSubIssues == subIssuesOverride;
    }

    /// <summary>
    /// Last-write-wins: when two overrides set the same field, the later one dominates.
    /// ApplyProjectOverrides(ApplyProjectOverrides(base, A), B) uses B's value.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool OverlappingOverrides_LastWriteWins(
        PositiveInt retriesA,
        PositiveInt retriesB)
    {
        var baseConfig = TestPipelineConfig.Default();

        var projectA = new PipelineProject { Id = "proj-a", Name = "A", MaxRetries = retriesA.Get };
        var projectB = new PipelineProject { Id = "proj-b", Name = "B", MaxRetries = retriesB.Get };

        var afterA = PipelineConfiguration.ApplyProjectOverrides(baseConfig, projectA);
        var afterBoth = PipelineConfiguration.ApplyProjectOverrides(afterA, projectB);

        return afterBoth.MaxRetries == retriesB.Get;
    }

    /// <summary>
    /// Crash-freedom: ApplyProjectOverrides never throws for any combination of override values.
    /// The method has a try/catch for ArgumentOutOfRangeException — this verifies it handles
    /// edge cases gracefully.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool ApplyProjectOverrides_NeverThrows(
        int maxRetries,
        bool? analysisEnabled,
        bool? brainReadOnly)
    {
        var baseConfig = TestPipelineConfig.Default();
        var project = new PipelineProject
        {
            Id = "fuzz",
            Name = "FuzzProject",
            MaxRetries = maxRetries,
            AnalysisReviewEnabled = analysisEnabled,
            BrainReadOnly = brainReadOnly
        };

        // Should never throw — the guard clause catches ArgumentOutOfRangeException
        var result = PipelineConfiguration.ApplyProjectOverrides(baseConfig, project);

        return result is not null;
    }

    /// <summary>
    /// BlacklistedPaths REPLACE semantics: project blacklist completely replaces global,
    /// not merges with it.
    /// </summary>
    [Fact]
    public void BlacklistOverride_ReplacesRatherThanMerges()
    {
        var baseConfig = TestPipelineConfig.Default() with
        {
            BlacklistedPaths = new[] { ".agent", ".github", "node_modules" }
        };

        var project = new PipelineProject
        {
            Id = "proj-bl",
            Name = "BL",
            BlacklistedPaths = new[] { "vendor/", "dist/" }
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(baseConfig, project);

        result.BlacklistedPaths.Should().BeEquivalentTo(new[] { "vendor/", "dist/" });
        result.BlacklistedPaths.Should().NotContain(".agent");
    }

    /// <summary>
    /// CodeReview deep-merge semantics: non-null CodeReview override deep-merges with global.
    /// </summary>
    [Fact]
    public void CodeReviewOverride_DeepMergesWithGlobal()
    {
        var baseConfig = TestPipelineConfig.Default() with
        {
            CodeReview = new CodeReviewConfiguration { MaxIterations = 5, FixPrompt = "original" }
        };

        var project = new PipelineProject
        {
            Id = "proj-cr",
            Name = "CR",
            CodeReview = new CodeReviewOverrides { MaxIterations = 1 }
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(baseConfig, project);

        // MaxIterations overridden, FixPrompt preserved from global
        result.CodeReview.MaxIterations.Should().Be(1);
        result.CodeReview.FixPrompt.Should().Be("original");
    }

    private static PipelineProject CreateOverrideProject(int maxRetries, bool analysisReviewEnabled, bool hasCodeReview)
    {
        return new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "PropertyTestProject",
            MaxRetries = maxRetries,
            AgentTimeout = TimeSpan.FromMinutes(20),
            AnalysisReviewEnabled = analysisReviewEnabled,
            CodeReview = hasCodeReview
                ? new CodeReviewOverrides { MaxIterations = 3, FixPrompt = "fix it" }
                : null
        };
    }
}
