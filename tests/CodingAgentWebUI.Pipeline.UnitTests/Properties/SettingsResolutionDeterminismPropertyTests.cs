// Feature: 029-pipeline-projects
// Property 3: Settings Resolution Determinism
// Verify ApplyProjectOverrides(config, project) always produces the same output for same inputs.
using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for Settings Resolution Determinism.
/// Verifies that ApplyProjectOverrides is a pure function (referential transparency):
/// calling it with the same inputs always produces the same output.
/// **Validates: Requirements 3.4, 4.4, 15.3**
/// </summary>
public class SettingsResolutionDeterminismPropertyTests
{
    /// <summary>
    /// Property 3: Settings Resolution Determinism — same inputs always produce same output.
    /// For any random PipelineConfiguration and PipelineProject, calling ApplyProjectOverrides
    /// twice with the same arguments produces identical results (referential transparency).
    /// **Validates: Requirements 3.4, 4.4, 15.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SettingsResolutionArbitraries) })]
    public void ApplyProjectOverrides_IsDeterministic_SameInputsSameOutput(
        SettingsResolutionInput input)
    {
        var result1 = PipelineConfiguration.ApplyProjectOverrides(input.Config, input.Project);
        var result2 = PipelineConfiguration.ApplyProjectOverrides(input.Config, input.Project);

        // All behavioral fields must be identical between both calls
        result1.MaxRetries.Should().Be(result2.MaxRetries);
        result1.MaxAnalysisRetries.Should().Be(result2.MaxAnalysisRetries);
        result1.AgentTimeout.Should().Be(result2.AgentTimeout);
        result1.AnalysisPrompt.Should().Be(result2.AnalysisPrompt);
        result1.ImplementationPrompt.Should().Be(result2.ImplementationPrompt);
        result1.AnalysisReviewEnabled.Should().Be(result2.AnalysisReviewEnabled);
        result1.AnalysisReviewPrompt.Should().Be(result2.AnalysisReviewPrompt);
        result1.AnalysisRefinementPrompt.Should().Be(result2.AnalysisRefinementPrompt);
        result1.CodeReview.Should().Be(result2.CodeReview);
        result1.BaselineHealthCheckEnabled.Should().Be(result2.BaselineHealthCheckEnabled);
        result1.ExternalCiTimeout.Should().Be(result2.ExternalCiTimeout);
        result1.ExternalCiPollInterval.Should().Be(result2.ExternalCiPollInterval);
        result1.MaxInfrastructureRetries.Should().Be(result2.MaxInfrastructureRetries);
        result1.StallWarningInterval.Should().Be(result2.StallWarningInterval);
        result1.MaxDecompositionSubIssues.Should().Be(result2.MaxDecompositionSubIssues);
        result1.MaxConcurrentDecompositions.Should().Be(result2.MaxConcurrentDecompositions);
        result1.DecompositionTimeout.Should().Be(result2.DecompositionTimeout);
        result1.MaxOpenIssuesForContext.Should().Be(result2.MaxOpenIssuesForContext);
        result1.MaxRefactoringProposals.Should().Be(result2.MaxRefactoringProposals);
        result1.RefactoringReviewEnabled.Should().Be(result2.RefactoringReviewEnabled);
        result1.BrainConsolidationReviewEnabled.Should().Be(result2.BrainConsolidationReviewEnabled);
        result1.HarnessSuggestionsReviewEnabled.Should().Be(result2.HarnessSuggestionsReviewEnabled);
        result1.BlacklistedPaths.Should().BeEquivalentTo(result2.BlacklistedPaths);
        result1.BrainReadOnly.Should().Be(result2.BrainReadOnly);
    }

    /// <summary>
    /// Property 3b: Null project returns config unchanged.
    /// When project is null, ApplyProjectOverrides returns the original config unmodified.
    /// **Validates: Requirements 3.4, 4.4, 15.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SettingsResolutionArbitraries) })]
    public void ApplyProjectOverrides_NullProject_ReturnsConfigUnchanged(
        SettingsResolutionInput input)
    {
        var result = PipelineConfiguration.ApplyProjectOverrides(input.Config, null);

        // Config must be identical to original when project is null
        result.MaxRetries.Should().Be(input.Config.MaxRetries);
        result.MaxAnalysisRetries.Should().Be(input.Config.MaxAnalysisRetries);
        result.AgentTimeout.Should().Be(input.Config.AgentTimeout);
        result.AnalysisPrompt.Should().Be(input.Config.AnalysisPrompt);
        result.ImplementationPrompt.Should().Be(input.Config.ImplementationPrompt);
        result.AnalysisReviewEnabled.Should().Be(input.Config.AnalysisReviewEnabled);
        result.AnalysisReviewPrompt.Should().Be(input.Config.AnalysisReviewPrompt);
        result.AnalysisRefinementPrompt.Should().Be(input.Config.AnalysisRefinementPrompt);
        result.CodeReview.Should().Be(input.Config.CodeReview);
        result.BaselineHealthCheckEnabled.Should().Be(input.Config.BaselineHealthCheckEnabled);
        result.ExternalCiTimeout.Should().Be(input.Config.ExternalCiTimeout);
        result.ExternalCiPollInterval.Should().Be(input.Config.ExternalCiPollInterval);
        result.MaxInfrastructureRetries.Should().Be(input.Config.MaxInfrastructureRetries);
        result.StallWarningInterval.Should().Be(input.Config.StallWarningInterval);
        result.MaxDecompositionSubIssues.Should().Be(input.Config.MaxDecompositionSubIssues);
        result.MaxConcurrentDecompositions.Should().Be(input.Config.MaxConcurrentDecompositions);
        result.DecompositionTimeout.Should().Be(input.Config.DecompositionTimeout);
        result.MaxOpenIssuesForContext.Should().Be(input.Config.MaxOpenIssuesForContext);
        result.MaxRefactoringProposals.Should().Be(input.Config.MaxRefactoringProposals);
        result.RefactoringReviewEnabled.Should().Be(input.Config.RefactoringReviewEnabled);
        result.BrainConsolidationReviewEnabled.Should().Be(input.Config.BrainConsolidationReviewEnabled);
        result.HarnessSuggestionsReviewEnabled.Should().Be(input.Config.HarnessSuggestionsReviewEnabled);
        result.BlacklistedPaths.Should().BeEquivalentTo(input.Config.BlacklistedPaths);
        result.BrainReadOnly.Should().Be(input.Config.BrainReadOnly);
    }

    /// <summary>
    /// Property 3c: Null project fields leave corresponding config fields unchanged.
    /// When a project field is null, the corresponding config field retains its original value.
    /// **Validates: Requirements 3.2, 3.4, 4.4, 15.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SettingsResolutionArbitraries) })]
    public void ApplyProjectOverrides_NullFields_LeaveConfigFieldsUnchanged(
        NullFieldsInput input)
    {
        // Project with all fields null (no overrides)
        var project = new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "NullOverrides"
        };

        var result = PipelineConfiguration.ApplyProjectOverrides(input.Config, project);

        // All fields should remain unchanged
        result.MaxRetries.Should().Be(input.Config.MaxRetries);
        result.MaxAnalysisRetries.Should().Be(input.Config.MaxAnalysisRetries);
        result.AgentTimeout.Should().Be(input.Config.AgentTimeout);
        result.AnalysisPrompt.Should().Be(input.Config.AnalysisPrompt);
        result.ImplementationPrompt.Should().Be(input.Config.ImplementationPrompt);
        result.AnalysisReviewEnabled.Should().Be(input.Config.AnalysisReviewEnabled);
        result.AnalysisReviewPrompt.Should().Be(input.Config.AnalysisReviewPrompt);
        result.AnalysisRefinementPrompt.Should().Be(input.Config.AnalysisRefinementPrompt);
        result.CodeReview.Should().Be(input.Config.CodeReview);
        result.BaselineHealthCheckEnabled.Should().Be(input.Config.BaselineHealthCheckEnabled);
        result.ExternalCiTimeout.Should().Be(input.Config.ExternalCiTimeout);
        result.ExternalCiPollInterval.Should().Be(input.Config.ExternalCiPollInterval);
        result.MaxInfrastructureRetries.Should().Be(input.Config.MaxInfrastructureRetries);
        result.StallWarningInterval.Should().Be(input.Config.StallWarningInterval);
        result.MaxDecompositionSubIssues.Should().Be(input.Config.MaxDecompositionSubIssues);
        result.MaxConcurrentDecompositions.Should().Be(input.Config.MaxConcurrentDecompositions);
        result.DecompositionTimeout.Should().Be(input.Config.DecompositionTimeout);
        result.MaxOpenIssuesForContext.Should().Be(input.Config.MaxOpenIssuesForContext);
        result.MaxRefactoringProposals.Should().Be(input.Config.MaxRefactoringProposals);
        result.RefactoringReviewEnabled.Should().Be(input.Config.RefactoringReviewEnabled);
        result.BrainConsolidationReviewEnabled.Should().Be(input.Config.BrainConsolidationReviewEnabled);
        result.HarnessSuggestionsReviewEnabled.Should().Be(input.Config.HarnessSuggestionsReviewEnabled);
        result.BlacklistedPaths.Should().BeEquivalentTo(input.Config.BlacklistedPaths);
        result.BrainReadOnly.Should().Be(input.Config.BrainReadOnly);
    }

    /// <summary>
    /// Property 3d: Non-null project fields override corresponding config fields.
    /// When a project field is non-null, the result has the project's value for that field.
    /// **Validates: Requirements 3.3, 3.4, 4.4, 15.3**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = new[] { typeof(SettingsResolutionArbitraries) })]
    public void ApplyProjectOverrides_NonNullFields_OverrideConfigFields(
        SettingsResolutionInput input)
    {
        var project = input.Project;
        if (project is null) return; // Skip null project cases (covered by 3b)

        var result = PipelineConfiguration.ApplyProjectOverrides(input.Config, project);

        // Each non-null project field must override the config field
        if (project.MaxRetries.HasValue)
            result.MaxRetries.Should().Be(project.MaxRetries.Value);
        if (project.MaxAnalysisRetries.HasValue)
            result.MaxAnalysisRetries.Should().Be(project.MaxAnalysisRetries.Value);
        if (project.AgentTimeout.HasValue)
            result.AgentTimeout.Should().Be(project.AgentTimeout.Value);
        if (project.AnalysisPrompt is not null)
            result.AnalysisPrompt.Should().Be(project.AnalysisPrompt);
        if (project.ImplementationPrompt is not null)
            result.ImplementationPrompt.Should().Be(project.ImplementationPrompt);
        if (project.AnalysisReviewEnabled.HasValue)
            result.AnalysisReviewEnabled.Should().Be(project.AnalysisReviewEnabled.Value);
        if (project.AnalysisReviewPrompt is not null)
            result.AnalysisReviewPrompt.Should().Be(project.AnalysisReviewPrompt);
        if (project.AnalysisRefinementPrompt is not null)
            result.AnalysisRefinementPrompt.Should().Be(project.AnalysisRefinementPrompt);
        if (project.CodeReview is not null)
        {
            // TODO: Assertion block does not verify InlineComments sub-properties after
            // override. When GenCodeReviewOverrides is updated to produce InlineComments,
            // add assertions for InlineComments deep-merge results here.
            if (project.CodeReview.MaxIterations.HasValue)
                result.CodeReview.MaxIterations.Should().Be(project.CodeReview.MaxIterations.Value);
            if (project.CodeReview.FixPrompt is not null)
                result.CodeReview.FixPrompt.Should().Be(project.CodeReview.FixPrompt);
            if (project.CodeReview.ReviewIsolation.HasValue)
                result.CodeReview.ReviewIsolation.Should().Be(project.CodeReview.ReviewIsolation.Value);
        }
        if (project.BaselineHealthCheckEnabled.HasValue)
            result.BaselineHealthCheckEnabled.Should().Be(project.BaselineHealthCheckEnabled.Value);
        if (project.ExternalCiTimeout.HasValue)
            result.ExternalCiTimeout.Should().Be(project.ExternalCiTimeout.Value);
        if (project.ExternalCiPollInterval.HasValue)
            result.ExternalCiPollInterval.Should().Be(project.ExternalCiPollInterval.Value);
        if (project.MaxInfrastructureRetries.HasValue)
            result.MaxInfrastructureRetries.Should().Be(project.MaxInfrastructureRetries.Value);
        if (project.StallWarningInterval.HasValue)
            result.StallWarningInterval.Should().Be(project.StallWarningInterval.Value);
        if (project.MaxDecompositionSubIssues.HasValue)
            result.MaxDecompositionSubIssues.Should().Be(project.MaxDecompositionSubIssues.Value);
        if (project.MaxConcurrentDecompositions.HasValue)
            result.MaxConcurrentDecompositions.Should().Be(project.MaxConcurrentDecompositions.Value);
        if (project.DecompositionTimeout.HasValue)
            result.DecompositionTimeout.Should().Be(project.DecompositionTimeout.Value);
        if (project.MaxOpenIssuesForContext.HasValue)
            result.MaxOpenIssuesForContext.Should().Be(project.MaxOpenIssuesForContext.Value);
        if (project.MaxRefactoringProposals.HasValue)
            result.MaxRefactoringProposals.Should().Be(project.MaxRefactoringProposals.Value);
        if (project.RefactoringReviewEnabled.HasValue)
            result.RefactoringReviewEnabled.Should().Be(project.RefactoringReviewEnabled.Value);
        if (project.BrainConsolidationReviewEnabled.HasValue)
            result.BrainConsolidationReviewEnabled.Should().Be(project.BrainConsolidationReviewEnabled.Value);
        if (project.HarnessSuggestionsReviewEnabled.HasValue)
            result.HarnessSuggestionsReviewEnabled.Should().Be(project.HarnessSuggestionsReviewEnabled.Value);
        if (project.BlacklistedPaths is not null)
            result.BlacklistedPaths.Should().BeEquivalentTo(project.BlacklistedPaths);
        if (project.BrainReadOnly.HasValue)
            result.BrainReadOnly.Should().Be(project.BrainReadOnly.Value);
    }
}

// --- Input wrapper types for FsCheck ---

/// <summary>Input for settings resolution determinism property tests.</summary>
public sealed class SettingsResolutionInput
{
    public required PipelineConfiguration Config { get; init; }
    public required PipelineProject? Project { get; init; }

    public override string ToString() =>
        $"Config(MaxRetries={Config.MaxRetries}), Project={Project?.Name ?? "null"}";
}

/// <summary>Input for null-fields property test — generates configs with random values.</summary>
public sealed class NullFieldsInput
{
    public required PipelineConfiguration Config { get; init; }

    public override string ToString() =>
        $"Config(MaxRetries={Config.MaxRetries}, AgentTimeout={Config.AgentTimeout})";
}

// --- Arbitrary generators ---

/// <summary>
/// FsCheck generators for settings resolution determinism tests.
/// Generates random PipelineConfiguration and PipelineProject instances
/// with various nullable override fields set.
/// </summary>
public class SettingsResolutionArbitraries
{
    private static readonly string[] PromptPool =
        ["Analyze code", "Implement feature", "Review analysis", "Refine analysis", "Fix bugs"];

    private static readonly string[] PathPool =
        ["bin/", "obj/", "node_modules/", ".git/", "*.log", "dist/", "coverage/"];

    private static Gen<string> GenPrompt() =>
        Gen.Elements(PromptPool);

    private static Gen<TimeSpan> GenTimeSpan() =>
        Gen.Choose(1, 120).Select(minutes => TimeSpan.FromMinutes(minutes));

    private static Gen<CodeReviewOverrides> GenCodeReviewOverrides() =>
        // TODO: Generator never produces InlineComments overrides (always null), so the
        // property-based test never exercises the InlineComments deep-merge path.
        // Add occasional non-null InlineCommentOverrides to improve coverage.
        from maxIterations in Gen.Elements<int?>(null, 1, 2, 3, 5)
        from fixPrompt in Gen.Elements<string?>(null, "Fix the issues", "Apply corrections")
        from isolation in Gen.Elements<ReviewIsolation?>(null, ReviewIsolation.Shared, ReviewIsolation.Isolated)
        select new CodeReviewOverrides
        {
            MaxIterations = maxIterations,
            FixPrompt = fixPrompt,
            ReviewIsolation = isolation
        };

    private static Gen<CodeReviewConfiguration> GenCodeReview() =>
        from maxIterations in Gen.Choose(1, 5)
        from fixPrompt in Gen.Elements<string?>(null, "Fix the issues", "Apply corrections")
        select new CodeReviewConfiguration
        {
            MaxIterations = maxIterations,
            FixPrompt = fixPrompt
        };

    private static Gen<IReadOnlyList<string>> GenBlacklistedPaths() =>
        from count in Gen.Choose(0, 4)
        from paths in Gen.ArrayOf(Gen.Elements(PathPool)).Resize(count)
        select (IReadOnlyList<string>)paths.Distinct().ToList();

    private static Gen<PipelineConfiguration> GenConfig() =>
        from maxRetries in Gen.Choose(0, 5)
        from maxAnalysisRetries in Gen.Choose(0, 3)
        from agentTimeout in GenTimeSpan()
        from analysisPrompt in GenPrompt()
        from implementationPrompt in GenPrompt()
        from analysisReviewEnabled in Gen.Elements(true, false)
        from analysisReviewPrompt in GenPrompt()
        from analysisRefinementPrompt in GenPrompt()
        from codeReview in GenCodeReview()
        from baselineHealthCheckEnabled in Gen.Elements(true, false)
        from externalCiTimeout in GenTimeSpan()
        from externalCiPollInterval in GenTimeSpan()
        from maxInfraRetries in Gen.Choose(0, 5)
        from stallWarningInterval in GenTimeSpan()
        from maxDecompSubIssues in Gen.Choose(1, 20)
        from maxConcurrentDecomps in Gen.Choose(1, 5)
        from decompTimeout in GenTimeSpan()
        from maxOpenIssues in Gen.Choose(1, 100)
        from maxRefactoringProposals in Gen.Choose(1, 10)
        from refactoringReviewEnabled in Gen.Elements(true, false)
        from brainConsolidationReviewEnabled in Gen.Elements(true, false)
        from harnessSuggestionsReviewEnabled in Gen.Elements(true, false)
        from blacklistedPaths in GenBlacklistedPaths()
        from brainReadOnly in Gen.Elements(true, false)
        select new PipelineConfiguration
        {
            MaxRetries = maxRetries,
            MaxAnalysisRetries = maxAnalysisRetries,
            AgentTimeout = agentTimeout,
            AnalysisPrompt = analysisPrompt,
            ImplementationPrompt = implementationPrompt,
            AnalysisReviewEnabled = analysisReviewEnabled,
            AnalysisReviewPrompt = analysisReviewPrompt,
            AnalysisRefinementPrompt = analysisRefinementPrompt,
            CodeReview = codeReview,
            BaselineHealthCheckEnabled = baselineHealthCheckEnabled,
            ExternalCiTimeout = externalCiTimeout,
            ExternalCiPollInterval = externalCiPollInterval,
            MaxInfrastructureRetries = maxInfraRetries,
            StallWarningInterval = stallWarningInterval,
            MaxDecompositionSubIssues = maxDecompSubIssues,
            MaxConcurrentDecompositions = maxConcurrentDecomps,
            DecompositionTimeout = decompTimeout,
            MaxOpenIssuesForContext = maxOpenIssues,
            MaxRefactoringProposals = maxRefactoringProposals,
            RefactoringReviewEnabled = refactoringReviewEnabled,
            BrainConsolidationReviewEnabled = brainConsolidationReviewEnabled,
            HarnessSuggestionsReviewEnabled = harnessSuggestionsReviewEnabled,
            BlacklistedPaths = blacklistedPaths,
            BrainReadOnly = brainReadOnly
        };

    private static Gen<PipelineProject?> GenProject() =>
        Gen.Frequency(
            (1, Gen.Constant<PipelineProject?>(null)),
            (4, GenNonNullProject().Select<PipelineProject, PipelineProject?>(p => p)));

    private static Gen<PipelineProject> GenNonNullProject() =>
        from maxRetries in Gen.Elements<int?>(null, 1, 2, 3, 5)
        from maxAnalysisRetries in Gen.Elements<int?>(null, 0, 1, 2)
        from agentTimeout in Gen.Elements<TimeSpan?>(null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60))
        from analysisPrompt in Gen.Elements<string?>(null, "Custom analysis", "Override prompt")
        from implementationPrompt in Gen.Elements<string?>(null, "Custom impl", "Build it")
        from analysisReviewEnabled in Gen.Elements<bool?>(null, true, false)
        from analysisReviewPrompt in Gen.Elements<string?>(null, "Custom review")
        from analysisRefinementPrompt in Gen.Elements<string?>(null, "Custom refinement")
        from codeReview in Gen.Frequency(
            (2, Gen.Constant<CodeReviewOverrides?>(null)),
            (1, GenCodeReviewOverrides().Select<CodeReviewOverrides, CodeReviewOverrides?>(cr => cr)))
        from baselineHealthCheckEnabled in Gen.Elements<bool?>(null, true, false)
        from externalCiTimeout in Gen.Elements<TimeSpan?>(null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30))
        from externalCiPollInterval in Gen.Elements<TimeSpan?>(null, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1))
        from maxInfraRetries in Gen.Elements<int?>(null, 1, 3)
        from stallWarningInterval in Gen.Elements<TimeSpan?>(null, TimeSpan.FromMinutes(5))
        from maxDecompSubIssues in Gen.Elements<int?>(null, 5, 10, 15)
        from maxConcurrentDecomps in Gen.Elements<int?>(null, 1, 2, 4)
        from decompTimeout in Gen.Elements<TimeSpan?>(null, TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(20))
        from maxOpenIssues in Gen.Elements<int?>(null, 25, 50)
        from maxRefactoringProposals in Gen.Elements<int?>(null, 2, 5)
        from refactoringReviewEnabled in Gen.Elements<bool?>(null, true, false)
        from brainConsolidationReviewEnabled in Gen.Elements<bool?>(null, true, false)
        from harnessSuggestionsReviewEnabled in Gen.Elements<bool?>(null, true, false)
        from blacklistedPaths in Gen.Frequency(
            (2, Gen.Constant<IReadOnlyList<string>?>(null)),
            (1, GenBlacklistedPaths().Select<IReadOnlyList<string>, IReadOnlyList<string>?>(p => p)))
        from brainReadOnly in Gen.Elements<bool?>(null, true, false)
        select new PipelineProject
        {
            Id = Guid.NewGuid().ToString(),
            Name = "TestProject",
            MaxRetries = maxRetries,
            MaxAnalysisRetries = maxAnalysisRetries,
            AgentTimeout = agentTimeout,
            AnalysisPrompt = analysisPrompt,
            ImplementationPrompt = implementationPrompt,
            AnalysisReviewEnabled = analysisReviewEnabled,
            AnalysisReviewPrompt = analysisReviewPrompt,
            AnalysisRefinementPrompt = analysisRefinementPrompt,
            CodeReview = codeReview,
            BaselineHealthCheckEnabled = baselineHealthCheckEnabled,
            ExternalCiTimeout = externalCiTimeout,
            ExternalCiPollInterval = externalCiPollInterval,
            MaxInfrastructureRetries = maxInfraRetries,
            StallWarningInterval = stallWarningInterval,
            MaxDecompositionSubIssues = maxDecompSubIssues,
            MaxConcurrentDecompositions = maxConcurrentDecomps,
            DecompositionTimeout = decompTimeout,
            MaxOpenIssuesForContext = maxOpenIssues,
            MaxRefactoringProposals = maxRefactoringProposals,
            RefactoringReviewEnabled = refactoringReviewEnabled,
            BrainConsolidationReviewEnabled = brainConsolidationReviewEnabled,
            HarnessSuggestionsReviewEnabled = harnessSuggestionsReviewEnabled,
            BlacklistedPaths = blacklistedPaths,
            BrainReadOnly = brainReadOnly
        };

    public static Arbitrary<SettingsResolutionInput> SettingsResolutionInputArb()
    {
        var gen =
            from config in GenConfig()
            from project in GenProject()
            select new SettingsResolutionInput
            {
                Config = config,
                Project = project
            };

        return gen.ToArbitrary();
    }

    public static Arbitrary<NullFieldsInput> NullFieldsInputArb()
    {
        var gen = GenConfig().Select(c => new NullFieldsInput { Config = c });
        return gen.ToArbitrary();
    }
}
