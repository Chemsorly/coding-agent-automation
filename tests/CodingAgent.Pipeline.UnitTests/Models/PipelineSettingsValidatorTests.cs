using System.Reflection;
using AwesomeAssertions;
using CodingAgent.Pipeline.CodeReview.Models;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineSettingsValidator"/>: the settings' [Range]s are the one source of their limits, for the
/// API's save and import checks and for the resolver, which skips a stored project override that is out of range.
/// </summary>
public class PipelineSettingsValidatorTests
{
    [Fact]
    public void Validate_Defaults_AreValid()
    {
        PipelineSettingsValidator.Validate(new PipelineConfiguration()).Should().BeEmpty();
    }

    [Fact]
    public void Validate_ReportsEachSettingOutsideItsRange()
    {
        var config = new PipelineConfiguration
        {
            MaxRetries = 11,
            StallWarningInterval = TimeSpan.FromSeconds(10),
            MaxImageSizeBytes = 1,
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 6,
                InlineComments = new InlineCommentSettings { MaxInlineComments = 0 },
            },
        };

        PipelineSettingsValidator.Validate(config).Should().BeEquivalentTo(
        [
            "MaxRetries must be between 0 and 10 (was 11).",
            "StallWarningInterval must be between 00:00:30 and 01:00:00 (was 00:00:10).",
            "MaxImageSizeBytes must be between 1048576 and 52428800 (was 1).",
            "CodeReview.MaxIterations must be between 0 and 5 (was 6).",
            "CodeReview.InlineComments.MaxInlineComments must be between 1 and 50 (was 0).",
        ]);
    }

    [Fact]
    public void ValidateOverrides_ReportsOnlyOverridesOutsideTheSettingsRange()
    {
        var project = new PipelineProject
        {
            Id = "p-1",
            Name = "Product",
            MaxRetries = 4,
            AgentTimeout = TimeSpan.FromSeconds(30),
            CodeReview = new CodeReviewOverrides
            {
                MaxIterations = 1,
                InlineComments = new InlineCommentOverrides { MaxRetries = 9, Enabled = false },
            },
        };

        PipelineSettingsValidator.ValidateOverrides(project).Should().BeEquivalentTo(
        [
            "AgentTimeout must be between 00:01:00 and 1.00:00:00 (was 00:00:30).",
            "CodeReview.InlineComments.MaxRetries must be between 0 and 5 (was 9).",
        ]);
    }

    [Fact]
    public void WithoutInvalidOverrides_AllValid_ReturnsTheProjectItself()
    {
        var project = new PipelineProject { Id = "p-1", Name = "Product", MaxRetries = 4, CodeReview = new CodeReviewOverrides { MaxIterations = 0 } };
        var errors = new List<string>();

        PipelineSettingsValidator.WithoutInvalidOverrides(project, errors).Should().BeSameAs(project);
        errors.Should().BeEmpty();
    }

    [Fact]
    public void WithoutInvalidOverrides_RemovesOnlyTheInvalidOverrides()
    {
        var project = new PipelineProject
        {
            Id = "p-1",
            Name = "Product",
            MaxRetries = 4,
            MaxDecompositionSubIssues = 25,
            CodeReview = new CodeReviewOverrides
            {
                MaxIterations = 1,
                InlineComments = new InlineCommentOverrides { MaxInlineComments = 500, OrderBySeverity = false },
            },
        };
        var errors = new List<string>();

        var result = PipelineSettingsValidator.WithoutInvalidOverrides(project, errors);

        result.MaxRetries.Should().Be(4);
        result.MaxDecompositionSubIssues.Should().BeNull();
        result.CodeReview!.MaxIterations.Should().Be(1);
        result.CodeReview.InlineComments!.MaxInlineComments.Should().BeNull();
        result.CodeReview.InlineComments.OrderBySeverity.Should().BeFalse();
        errors.Should().HaveCount(2);
        project.MaxDecompositionSubIssues.Should().Be(25, "the original project is not changed");
        project.CodeReview!.InlineComments!.MaxInlineComments.Should().Be(500);
    }

    [Fact]
    public void RangeOf_FindsTopLevelAndNestedSettings()
    {
        PipelineSettingsValidator.RangeOf(nameof(PipelineConfiguration.MaxRetries))!.Maximum.Should().Be(10);
        PipelineSettingsValidator.RangeOf("CodeReview.InlineComments.MaxInlineComments")!.Maximum.Should().Be(50);
        PipelineSettingsValidator.RangeOf(nameof(PipelineConfiguration.BrainReadOnly)).Should().BeNull();

        var act = () => PipelineSettingsValidator.RangeOf("NoSuchSetting");
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SettingPaths_ExpandTheCodeReviewSettings()
    {
        var paths = PipelineSettingsValidator.SettingPaths();

        paths.Should().Contain(["MaxRetries", "CodeReview.MaxIterations", "CodeReview.FixPrompt", "CodeReview.InlineComments.SeverityThreshold"]);
        paths.Should().NotContain("CodeReview");
    }

    [Fact]
    public void ProjectOverridablePaths_AreTheProjectOverridableSettings()
    {
        var paths = PipelineSettingsValidator.ProjectOverridablePaths();

        paths.Should().Contain(["AgentTimeout", "CodeReview.MaxIterations", "CodeReview.InlineComments.MaxRetries"]);
        paths.Should().NotContain(["MinIssueSlots", "MaxConcurrentDecompositions", "HarnessSuggestionsReviewEnabled", "ClosedLoopPollInterval"]);
    }

    [Fact]
    public void EveryNumberOrDurationSetting_DeclaresItsRange()
    {
        // TransientRetryDelay is internal: tests set it to zero, and no page offers it.
        var missing = PipelineSettingsValidator.SettingPaths()
            .Where(path => path != nameof(PipelineConfiguration.TransientRetryDelay))
            .Where(path => IsNumberOrDuration(SettingType(path)) && PipelineSettingsValidator.RangeOf(path) is null)
            .ToList();

        missing.Should().BeEmpty("a setting's limits are its [Range], which the API and the settings pages use");
    }

    private static Type SettingType(string path)
    {
        var type = typeof(PipelineConfiguration);
        foreach (var name in path.Split('.'))
            type = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public)!.PropertyType;
        return type;
    }

    private static bool IsNumberOrDuration(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(TimeSpan);
}
