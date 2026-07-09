using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using FsCheck;
using FsCheck.Xunit;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Property-based tests for PipelineConfiguration JSON serialization roundtrip.
/// Validates: Deserialize(Serialize(config)) preserves all JSON-visible property values.
/// Guards against regressions when new properties are added without correct serialization attributes.
/// </summary>
public class PipelineConfigurationJsonRoundtripPropertyTests
{
    private static readonly JsonSerializerOptions Options = PipelineJsonOptions.Default;

    /// <summary>
    /// Core roundtrip invariant: for any PipelineConfiguration with varied scalar properties,
    /// serializing then deserializing produces an equivalent configuration.
    /// Uses representative property mutations to exercise the full serialization surface.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool JsonRoundtrip_PreservesAllScalarProperties(
        PositiveInt maxRetries,
        PositiveInt maxAnalysisRetries,
        bool analysisReviewEnabled,
        bool acceptanceCriteriaEnabled,
        bool baselineHealthCheckEnabled,
        bool brainReadOnly,
        PositiveInt issuePageSize)
    {
        var config = new PipelineConfiguration
        {
            MaxRetries = maxRetries.Get,
            MaxAnalysisRetries = maxAnalysisRetries.Get,
            AnalysisReviewEnabled = analysisReviewEnabled,
            AcceptanceCriteriaEnabled = acceptanceCriteriaEnabled,
            BaselineHealthCheckEnabled = baselineHealthCheckEnabled,
            BrainReadOnly = brainReadOnly,
            IssuePageSize = issuePageSize.Get
        };

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        return deserialized is not null
            && deserialized.MaxRetries == config.MaxRetries
            && deserialized.MaxAnalysisRetries == config.MaxAnalysisRetries
            && deserialized.AnalysisReviewEnabled == config.AnalysisReviewEnabled
            && deserialized.AcceptanceCriteriaEnabled == config.AcceptanceCriteriaEnabled
            && deserialized.BaselineHealthCheckEnabled == config.BaselineHealthCheckEnabled
            && deserialized.BrainReadOnly == config.BrainReadOnly
            && deserialized.IssuePageSize == config.IssuePageSize;
    }

    /// <summary>
    /// TimeSpan roundtrip: TimeSpan properties survive serialization via the custom
    /// TimeSpanJsonConverter (ISO 8601 duration string format).
    /// </summary>
    [Property(MaxTest = 20)]
    public bool JsonRoundtrip_PreservesTimeSpanProperties(
        PositiveInt timeoutMinutes,
        PositiveInt pollSeconds,
        PositiveInt stallMinutes)
    {
        var agentTimeout = TimeSpan.FromMinutes(timeoutMinutes.Get % 120 + 1);
        var pollInterval = TimeSpan.FromSeconds(pollSeconds.Get % 300 + 1);
        var stallInterval = TimeSpan.FromMinutes(stallMinutes.Get % 60 + 1);

        var config = new PipelineConfiguration
        {
            AgentTimeout = agentTimeout,
            ExternalCiPollInterval = pollInterval,
            StallWarningInterval = stallInterval
        };

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        return deserialized is not null
            && deserialized.AgentTimeout == config.AgentTimeout
            && deserialized.ExternalCiPollInterval == config.ExternalCiPollInterval
            && deserialized.StallWarningInterval == config.StallWarningInterval;
    }

    /// <summary>
    /// String properties (prompts) roundtrip: ensures arbitrary non-null strings survive.
    /// </summary>
    [Property(MaxTest = 20)]
    public bool JsonRoundtrip_PreservesStringProperties(NonNull<string> analysisPrompt)
    {
        var config = new PipelineConfiguration
        {
            AnalysisPrompt = analysisPrompt.Get
        };

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        return deserialized is not null
            && deserialized.AnalysisPrompt == config.AnalysisPrompt;
    }

    /// <summary>
    /// Collection properties roundtrip: BlacklistedPaths (IReadOnlyList&lt;string&gt;)
    /// and LastUsedProviderIds (IReadOnlyDictionary&lt;string,string&gt;) survive serialization.
    /// </summary>
    [Fact]
    public void JsonRoundtrip_PreservesCollectionProperties()
    {
        var config = new PipelineConfiguration
        {
            BlacklistedPaths = new[] { ".env", "secrets.json", "node_modules" },
            LastUsedProviderIds = new Dictionary<string, string>
            {
                ["issue"] = "ip-1",
                ["repository"] = "rp-1",
                ["agent"] = "ap-1"
            }
        };

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.BlacklistedPaths.Should().BeEquivalentTo(config.BlacklistedPaths);
        deserialized.LastUsedProviderIds.Should().BeEquivalentTo(config.LastUsedProviderIds);
    }

    /// <summary>
    /// Nested CodeReviewConfiguration roundtrip: the entire nested record survives.
    /// </summary>
    [Fact]
    public void JsonRoundtrip_PreservesCodeReviewConfiguration()
    {
        var config = new PipelineConfiguration
        {
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 5,
                FixPrompt = "Custom fix prompt for testing"
            }
        };

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.CodeReview.Should().NotBeNull();
        deserialized.CodeReview.MaxIterations.Should().Be(config.CodeReview.MaxIterations);
        deserialized.CodeReview.FixPrompt.Should().Be(config.CodeReview.FixPrompt);
    }

    /// <summary>
    /// Default instance roundtrip: a default-constructed PipelineConfiguration survives
    /// serialization without data loss. Catches issues where default values differ between
    /// C# initialization and JSON deserialization.
    /// </summary>
    [Fact]
    public void JsonRoundtrip_DefaultInstance_PreservesAllDefaults()
    {
        var config = new PipelineConfiguration();

        var json = JsonSerializer.Serialize(config, Options);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, Options);

        deserialized.Should().NotBeNull();
        deserialized!.MaxRetries.Should().Be(config.MaxRetries);
        deserialized.AgentTimeout.Should().Be(config.AgentTimeout);
        deserialized.AnalysisReviewEnabled.Should().Be(config.AnalysisReviewEnabled);
        deserialized.AcceptanceCriteriaEnabled.Should().Be(config.AcceptanceCriteriaEnabled);
        deserialized.BaselineHealthCheckEnabled.Should().Be(config.BaselineHealthCheckEnabled);
        deserialized.ClosedLoopAutoStart.Should().Be(config.ClosedLoopAutoStart);
        deserialized.ClosedLoopPollInterval.Should().Be(config.ClosedLoopPollInterval);
        deserialized.ExternalCiTimeout.Should().Be(config.ExternalCiTimeout);
        deserialized.IssuePageSize.Should().Be(config.IssuePageSize);
        deserialized.BrainReadOnly.Should().Be(config.BrainReadOnly);
        deserialized.RefactoringReviewEnabled.Should().Be(config.RefactoringReviewEnabled);
        deserialized.BrainConsolidationReviewEnabled.Should().Be(config.BrainConsolidationReviewEnabled);
        deserialized.HarnessSuggestionsReviewEnabled.Should().Be(config.HarnessSuggestionsReviewEnabled);
    }

    /// <summary>
    /// Lenient deserialization: verifies that PipelineJsonOptions.Lenient can deserialize
    /// JSON produced by PipelineJsonOptions.Default (cross-options compatibility).
    /// This is the actual production path: Default writes to disk, Lenient reads from disk.
    /// </summary>
    [Fact]
    public void LenientDeserialization_CanReadDefaultSerialized()
    {
        var config = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(45),
            AnalysisReviewEnabled = false
        };

        var json = JsonSerializer.Serialize(config, PipelineJsonOptions.Default);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, PipelineJsonOptions.Lenient);

        deserialized.Should().NotBeNull();
        deserialized!.MaxRetries.Should().Be(7);
        deserialized.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        deserialized.AnalysisReviewEnabled.Should().BeFalse();
    }
}
