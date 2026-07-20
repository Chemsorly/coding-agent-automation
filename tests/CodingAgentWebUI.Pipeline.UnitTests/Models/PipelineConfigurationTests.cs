using System.Reflection;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using MessagePack;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Characterization tests for <see cref="PipelineConfiguration"/>.
/// Covers serialization round-trips, default value preservation, ApplyProjectOverrides
/// mutation safety, partial-apply semantics, ordering guarantees, and sub-config delegation.
/// </summary>
public class PipelineConfigurationTests
{
    private static readonly JsonSerializerOptions JsonOptions = PipelineJsonOptions.Default;

    // ── Serialization ──────────────────────────────────────────────────────────

    [Fact]
    public void SerializationRoundTrip_FullyPopulatedConfig_PreservesAllProperties()
    {
        // Arrange: set EVERY JSON-visible property to a non-default value
        var config = new PipelineConfiguration
        {
            MaxRetries = 7,
            MaxAnalysisRetries = 4,
            IssuePageSize = 50,
            AgentTimeout = TimeSpan.FromMinutes(45),
            WorkspaceBaseDirectory = "/custom/workspaces",
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 5,
                FixPrompt = "Custom fix prompt",
                ReviewIsolation = ReviewIsolation.Shared,
                InlineComments = new InlineCommentSettings
                {
                    Enabled = false,
                    MaxInlineComments = 25,
                    MaxRetries = 3,
                    OrderBySeverity = false,
                    SeverityThreshold = FindingSeverity.Critical,
                },
            },
            AnalysisPrompt = "Custom analysis prompt",
            ImplementationPrompt = "Custom implementation prompt",
            AnalysisReviewEnabled = false,
            AnalysisReviewPrompt = "Custom review prompt",
            AnalysisRefinementPrompt = "Custom refinement prompt",
            AcceptanceCriteriaEnabled = false,
            AcceptanceCriteriaPrompt = "Custom AC prompt",
            RefactoringReviewEnabled = false,
            BrainConsolidationReviewEnabled = false,
            HarnessSuggestionsReviewEnabled = false,
            BaselineHealthCheckEnabled = false,
            ExternalCiTimeout = TimeSpan.FromMinutes(25),
            ExternalCiPollInterval = TimeSpan.FromSeconds(45),
            MaxInfrastructureRetries = 4,
            CiNotStartedTimeout = TimeSpan.FromMinutes(8),
            CiNotStartedMaxRetries = 10,
            StallWarningInterval = TimeSpan.FromMinutes(5),
            StallPollInterval = TimeSpan.FromSeconds(15),
            BlacklistedPaths = new[] { "custom/path", "another/path" },
            PipelineInjectedPaths = new[] { ".injected" },
            AnalysisCommitThreshold = 50,
            FailedWorkspaceRetentionDays = 14,
            LastUsedProviderIds = new Dictionary<string, string>
            {
                ["issue"] = "ip-custom",
                ["repository"] = "rp-custom",
            },
            BrainReadOnly = true,
            ClosedLoopAutoStart = true,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
            ClosedLoopMaxRunsPerCycle = 5,
            ClosedLoopMaxConsecutivePollFailures = 10,
            ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(30),
            ClosedLoopMaxPagesToFetch = 20,
            ClosedLoopCircuitBreakerCooldown = TimeSpan.FromMinutes(10),
            DefaultRequiredAgentLabels = "kiro,dotnet",
            BrainPushMaxRetries = 5,
            AgentDisconnectGracePeriod = TimeSpan.FromMinutes(10),
            AgentBusyProgressTimeout = TimeSpan.FromMinutes(90),
            OutputBufferCapacity = 20_000,
            OutputLinesCapacity = 8_000,
            ChatHistoryCapacity = 300,
            QualityGateHistoryCapacity = 75,
            RetryErrorsCapacity = 150,
            HeartbeatSweepIntervalSeconds = 90,
            HeartbeatTimeoutSeconds = 120,
            OrphanedLabelSweepIntervalMinutes = 60,
            MaxRefactoringProposals = 5,
            HotspotAnalysisLookback = TimeSpan.FromDays(180),
            MaxDecompositionSubIssues = 15,
            MaxConcurrentDecompositions = 4,
            DecompositionTimeout = TimeSpan.FromMinutes(30),
            MaxOpenIssuesForContext = 100,
            RefactoringOutcomeLookback = TimeSpan.FromDays(180),
            MaxIssueImages = 20,
            MaxImageSizeBytes = 10_485_760,
            MaxTotalImageSizeBytes = 41_943_040,
            TotalImageDownloadTimeoutSeconds = 120,
            EnableIssueImageExtraction = false,
            EnableNativeImageParts = false,
            ImageDownloadTimeoutSeconds = 60,
        };

        // Act
        var json = JsonSerializer.Serialize(config, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, JsonOptions);

        // Assert: round-trip preserves all values
        deserialized.Should().NotBeNull();
        deserialized.Should().BeEquivalentTo(config);

        // Drift-detection: verify this test covers ALL [Key]-annotated properties.
        // When a new property is added with a [Key] attribute, this assertion fails,
        // forcing the developer to add it to the fully-populated config above.
        var keyPropertyCount = typeof(PipelineConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Count(p => p.GetCustomAttribute<KeyAttribute>() is not null);

        // Count the properties explicitly set above (all [Key] properties on the record).
        // If this fails, a new [Key] property was added — add it to the config above.
        keyPropertyCount.Should().Be(63,
            "this test must cover all [Key]-annotated properties on PipelineConfiguration. " +
            "If a new property was added, set it to a non-default value in the config above.");
    }

    // ── Default Values ─────────────────────────────────────────────────────────

    [Fact]
    public void DefaultValues_AllPropertiesMatchExpectedDefaults()
    {
        var config = new PipelineConfiguration();

        // Retry sub-config defaults
        config.MaxRetries.Should().Be(3);
        config.MaxAnalysisRetries.Should().Be(2);
        config.AgentTimeout.Should().Be(PipelineConstants.DefaultAgentTimeout);
        config.StallWarningInterval.Should().Be(PipelineConstants.DefaultStallWarningInterval);
        config.StallPollInterval.Should().Be(PipelineConstants.DefaultStallPollInterval);

        // ExternalCi sub-config defaults
        config.ExternalCiTimeout.Should().Be(PipelineConstants.DefaultExternalCiTimeout);
        config.ExternalCiPollInterval.Should().Be(PipelineConstants.DefaultExternalCiPollInterval);
        config.CiNotStartedTimeout.Should().Be(PipelineConstants.DefaultCiNotStartedTimeout);
        config.CiNotStartedMaxRetries.Should().Be(PipelineConstants.DefaultCiNotStartedMaxRetries);
        config.MaxInfrastructureRetries.Should().Be(5);

        // ClosedLoop sub-config defaults
        config.ClosedLoopAutoStart.Should().BeFalse();
        config.ClosedLoopPollInterval.Should().Be(PipelineConstants.DefaultClosedLoopPollInterval);
        config.ClosedLoopMaxRunsPerCycle.Should().Be(0);
        config.ClosedLoopMaxConsecutivePollFailures.Should().Be(5);
        config.ClosedLoopMaxBackoffInterval.Should().Be(PipelineConstants.DefaultClosedLoopMaxBackoffInterval);
        config.ClosedLoopMaxPagesToFetch.Should().Be(10);
        config.ClosedLoopCircuitBreakerCooldown.Should().Be(PipelineConstants.DefaultClosedLoopCircuitBreakerCooldown);

        // Agent sub-config defaults
        config.DefaultRequiredAgentLabels.Should().BeNull();
        config.BrainPushMaxRetries.Should().Be(3);
        config.AgentDisconnectGracePeriod.Should().Be(PipelineConstants.DefaultAgentDisconnectGracePeriod);
        config.AgentBusyProgressTimeout.Should().Be(PipelineConstants.DefaultAgentBusyProgressTimeout);
        config.OutputBufferCapacity.Should().Be(PipelineConstants.DefaultOutputBufferCapacity);
        config.OutputLinesCapacity.Should().Be(PipelineConstants.DefaultOutputLinesCapacity);
        config.ChatHistoryCapacity.Should().Be(PipelineConstants.DefaultChatHistoryCapacity);
        config.QualityGateHistoryCapacity.Should().Be(PipelineConstants.DefaultQualityGateHistoryCapacity);
        config.RetryErrorsCapacity.Should().Be(PipelineConstants.DefaultRetryErrorsCapacity);
        config.BrainReadOnly.Should().BeFalse();
        config.HeartbeatSweepIntervalSeconds.Should().Be(PipelineConstants.DefaultHeartbeatSweepIntervalSeconds);
        config.HeartbeatTimeoutSeconds.Should().Be(PipelineConstants.DefaultHeartbeatTimeoutSeconds);
        config.OrphanedLabelSweepIntervalMinutes.Should().Be(PipelineConstants.DefaultOrphanedLabelSweepIntervalMinutes);

        // Workspace sub-config defaults
        config.WorkspaceBaseDirectory.Should().Be("./workspaces");
        config.FailedWorkspaceRetentionDays.Should().Be(7);

        // Commit sub-config defaults
        config.BlacklistedPaths.Should().BeEquivalentTo(new[] { ".agent", ".brain" });

        // Direct properties with defaults
        config.IssuePageSize.Should().Be(25);
        config.AnalysisReviewEnabled.Should().BeTrue();
        config.AcceptanceCriteriaEnabled.Should().BeTrue();
        config.BaselineHealthCheckEnabled.Should().BeTrue();
        config.RefactoringReviewEnabled.Should().BeTrue();
        config.BrainConsolidationReviewEnabled.Should().BeTrue();
        config.HarnessSuggestionsReviewEnabled.Should().BeTrue();
        config.MaxRefactoringProposals.Should().Be(3);
        config.HotspotAnalysisLookback.Should().Be(TimeSpan.FromDays(90));
        config.MaxDecompositionSubIssues.Should().Be(10);
        config.MaxConcurrentDecompositions.Should().Be(2);
        config.DecompositionTimeout.Should().Be(TimeSpan.FromMinutes(15));
        config.MaxOpenIssuesForContext.Should().Be(50);
        config.RefactoringOutcomeLookback.Should().Be(TimeSpan.FromDays(90));
        config.AnalysisCommitThreshold.Should().Be(PipelineConstants.DefaultAnalysisCommitThreshold);
        config.PipelineInjectedPaths.Should().BeEmpty();
        config.LastUsedProviderIds.Should().BeEmpty();

        // Image settings defaults
        config.MaxIssueImages.Should().Be(10);
        config.MaxImageSizeBytes.Should().Be(5_242_880);
        config.MaxTotalImageSizeBytes.Should().Be(20_971_520);
        config.TotalImageDownloadTimeoutSeconds.Should().Be(60);
        config.EnableIssueImageExtraction.Should().BeTrue();
        config.EnableNativeImageParts.Should().BeTrue();
        config.ImageDownloadTimeoutSeconds.Should().Be(30);

        // CodeReview defaults
        config.CodeReview.Should().NotBeNull();
        config.CodeReview.MaxIterations.Should().Be(2);
        config.CodeReview.FixPrompt.Should().BeNull();
        config.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
        config.CodeReview.InlineComments.Enabled.Should().BeTrue();
    }

    // ── ApplyProjectOverrides — Scalars ────────────────────────────────────────

    // TODO: AnalysisReviewEnabled=true as override may not detect a regression if TestPipelineConfig.Default()
    // changes its base value to true — consider using a value that differs from both the class default and
    // the helper default to ensure the override logic is truly exercised.
    [Fact]
    public void ApplyProjectOverrides_ScalarOverrides_AppliedCorrectly()
    {
        var config = TestPipelineConfig.Default();
        var project = TestPipelineConfig.WithProject() with
        {
            MaxRetries = 10,
            AgentTimeout = TimeSpan.FromMinutes(60),
            AnalysisReviewEnabled = true,
            MaxDecompositionSubIssues = 15,
            BrainReadOnly = true,
        };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(10);
        result.AgentTimeout.Should().Be(TimeSpan.FromMinutes(60));
        result.AnalysisReviewEnabled.Should().BeTrue();
        result.MaxDecompositionSubIssues.Should().Be(15);
        result.BrainReadOnly.Should().BeTrue();
    }

    [Fact]
    public void ApplyProjectOverrides_NullOverrides_PreserveGlobalValues()
    {
        var config = TestPipelineConfig.Default() with
        {
            MaxRetries = 5,
            AgentTimeout = TimeSpan.FromMinutes(45),
            AnalysisReviewEnabled = true,
        };

        // Project with all-null behavioral fields
        var project = TestPipelineConfig.DefaultProject();

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.MaxRetries.Should().Be(5);
        result.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        result.AnalysisReviewEnabled.Should().BeTrue();
        // TODO: Only 3 scalar properties are verified here. If a new [ProjectOverridable] property
        // defaults to a non-null value on DefaultProject(), this test would miss a regression where
        // that property gets unexpectedly applied. Consider using BeEquivalentTo against the original
        // config or adding drift-detection similar to the serialization round-trip test.
        result.CodeReview.Should().BeSameAs(config.CodeReview);
    }

    // ── ApplyProjectOverrides — Deep-Merge ─────────────────────────────────────

    [Fact]
    public void ApplyProjectOverrides_PartialCodeReview_DeepMergesPreservingUnsetProperties()
    {
        var config = TestPipelineConfig.Default() with
        {
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 5,
                FixPrompt = "Global fix prompt",
                ReviewIsolation = ReviewIsolation.Isolated,
                InlineComments = new InlineCommentSettings
                {
                    Enabled = true,
                    MaxInlineComments = 20,
                    OrderBySeverity = true,
                },
            },
        };

        // Only override MaxIterations — everything else should be preserved
        var project = TestPipelineConfig.WithProject() with
        {
            CodeReview = new CodeReviewOverrides { MaxIterations = 3 },
        };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // Overridden property
        result.CodeReview.MaxIterations.Should().Be(3);

        // Preserved properties (deep-merge semantics)
        result.CodeReview.FixPrompt.Should().Be("Global fix prompt");
        result.CodeReview.ReviewIsolation.Should().Be(ReviewIsolation.Isolated);
        result.CodeReview.InlineComments.Enabled.Should().BeTrue();
        result.CodeReview.InlineComments.MaxInlineComments.Should().Be(20);
        result.CodeReview.InlineComments.OrderBySeverity.Should().BeTrue();
    }

    // ── ApplyProjectOverrides — Ordering ───────────────────────────────────────

    [Fact]
    public void ProjectOverridableAttributes_HaveUniqueAscendingOrderValues()
    {
        // Read all [ProjectOverridable] attributes from PipelineConfiguration
        var orderedAttributes = typeof(PipelineConfiguration)
            .GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Select(p => (Property: p, Attribute: p.GetCustomAttribute<ProjectOverridableAttribute>()))
            .Where(x => x.Attribute is not null)
            .Select(x => (x.Property.Name, x.Attribute!.Order))
            .OrderBy(x => x.Order)
            .ToList();

        // Must have at least one (sanity check)
        orderedAttributes.Should().NotBeEmpty();

        // All Order values must be unique (no duplicates)
        var orderValues = orderedAttributes.Select(x => x.Order).ToList();
        orderValues.Should().OnlyHaveUniqueItems(
            "duplicate [ProjectOverridable(Order = N)] values produce non-deterministic " +
            "iteration which breaks consistent logging and override application order");

        // Order values should be strictly ascending (validates that the sorted order
        // used by BuildOverrideMappings produces a deterministic sequence)
        for (var i = 1; i < orderValues.Count; i++)
        {
            orderValues[i].Should().BeGreaterThan(orderValues[i - 1],
                $"Order values must be strictly ascending. " +
                $"Property at index {i} has Order={orderValues[i]} which is not greater than " +
                $"previous Order={orderValues[i - 1]}");
        }
    }

    // ── ApplyProjectOverrides — Safety ─────────────────────────────────────────

    [Fact]
    public void ApplyProjectOverrides_DoesNotMutateOriginalConfig()
    {
        var config = TestPipelineConfig.Default() with
        {
            MaxRetries = 3,
            AnalysisReviewEnabled = false,
            CodeReview = new CodeReviewConfiguration
            {
                MaxIterations = 5,
                FixPrompt = "Original fix prompt",
                InlineComments = new InlineCommentSettings { MaxInlineComments = 10 },
            },
        };

        // Capture original values
        var originalMaxRetries = config.MaxRetries;
        var originalAnalysisReviewEnabled = config.AnalysisReviewEnabled;
        var originalCodeReview = config.CodeReview;
        var originalMaxIterations = config.CodeReview.MaxIterations;
        var originalFixPrompt = config.CodeReview.FixPrompt;
        var originalMaxInlineComments = config.CodeReview.InlineComments.MaxInlineComments;

        // Apply overrides that change both scalars and deep-merge properties
        var project = TestPipelineConfig.WithProject() with
        {
            MaxRetries = 10,
            AnalysisReviewEnabled = true,
            CodeReview = new CodeReviewOverrides
            {
                MaxIterations = 1,
                FixPrompt = "Overridden fix prompt",
            },
        };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // Result should have the overrides applied
        result.MaxRetries.Should().Be(10);
        result.CodeReview.MaxIterations.Should().Be(1);

        // Original config must NOT be mutated — this is the critical invariant
        config.MaxRetries.Should().Be(originalMaxRetries);
        config.AnalysisReviewEnabled.Should().Be(originalAnalysisReviewEnabled);

        // Deep-merge path: original CodeReview must be completely untouched
        config.CodeReview.Should().BeSameAs(originalCodeReview);
        config.CodeReview.MaxIterations.Should().Be(originalMaxIterations);
        config.CodeReview.FixPrompt.Should().Be(originalFixPrompt);
        config.CodeReview.InlineComments.MaxInlineComments.Should().Be(originalMaxInlineComments);
    }

    [Fact]
    public void ApplyProjectOverrides_ArgumentOutOfRange_ReturnsOriginalConfig()
    {
        var config = TestPipelineConfig.Default() with
        {
            MaxRetries = 3,
            MaxDecompositionSubIssues = 10,
        };

        // MaxRetries (Order=1) is valid; MaxDecompositionSubIssues (Order=18) is out of range 1-20.
        // On validation error, the entire partially-mutated clone is discarded and the original
        // config is returned unchanged.
        var project = TestPipelineConfig.WithProject() with
        {
            MaxRetries = 7,
            MaxDecompositionSubIssues = 25, // Out of range — triggers ArgumentOutOfRangeException
        };

        // Should NOT throw — the catch clause handles TargetInvocationException
        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        // Original config returned unchanged — no overrides applied (including valid ones)
        result.MaxRetries.Should().Be(3);
        result.MaxDecompositionSubIssues.Should().Be(10);

        // Verify referential identity — same object as input, not a clone
        result.Should().BeSameAs(config);
    }

    // ── ApplyProjectOverrides — Previously untested properties ─────────────────

    [Fact]
    public void ApplyProjectOverrides_AcceptanceCriteriaEnabled_OverridesCorrectly()
    {
        var config = TestPipelineConfig.Default() with { AcceptanceCriteriaEnabled = true };
        var project = TestPipelineConfig.WithProject() with { AcceptanceCriteriaEnabled = false };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AcceptanceCriteriaEnabled.Should().BeFalse();
    }

    [Fact]
    public void ApplyProjectOverrides_AnalysisCommitThreshold_OverridesCorrectly()
    {
        var config = TestPipelineConfig.Default() with { AnalysisCommitThreshold = 30 };
        var project = TestPipelineConfig.WithProject() with { AnalysisCommitThreshold = 50 };

        var result = PipelineConfigurationResolver.ApplyProjectOverrides(config, project);

        result.AnalysisCommitThreshold.Should().Be(50);
    }

    // ── Sub-config delegation ──────────────────────────────────────────────────

    [Fact]
    public void SubConfigDelegation_FlatPropertyPopulatesSubConfig()
    {
        // Verify that flat JSON-visible properties correctly delegate to sub-configs
        var config = new PipelineConfiguration
        {
            MaxRetries = 5,
            MaxAnalysisRetries = 2,
            AgentTimeout = TimeSpan.FromMinutes(45),
            StallWarningInterval = TimeSpan.FromMinutes(3),
            StallPollInterval = TimeSpan.FromSeconds(20),
        };

        // Retry sub-config should reflect the flat property values
        config.Retry.MaxRetries.Should().Be(5);
        config.Retry.MaxAnalysisRetries.Should().Be(2);
        config.Retry.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        config.Retry.StallWarningInterval.Should().Be(TimeSpan.FromMinutes(3));
        config.Retry.StallPollInterval.Should().Be(TimeSpan.FromSeconds(20));

        // ExternalCi sub-config
        var config2 = new PipelineConfiguration
        {
            ExternalCiTimeout = TimeSpan.FromMinutes(25),
            ExternalCiPollInterval = TimeSpan.FromSeconds(45),
            CiNotStartedTimeout = TimeSpan.FromMinutes(8),
            CiNotStartedMaxRetries = 10,
            MaxInfrastructureRetries = 4,
        };
        config2.ExternalCi.ExternalCiTimeout.Should().Be(TimeSpan.FromMinutes(25));
        config2.ExternalCi.ExternalCiPollInterval.Should().Be(TimeSpan.FromSeconds(45));
        config2.ExternalCi.CiNotStartedTimeout.Should().Be(TimeSpan.FromMinutes(8));
        config2.ExternalCi.CiNotStartedMaxRetries.Should().Be(10);
        config2.ExternalCi.MaxInfrastructureRetries.Should().Be(4);

        // ClosedLoop sub-config
        var config3 = new PipelineConfiguration
        {
            ClosedLoopAutoStart = true,
            ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
            ClosedLoopMaxRunsPerCycle = 5,
            ClosedLoopMaxConsecutivePollFailures = 8,
            ClosedLoopMaxPagesToFetch = 20,
            ClosedLoopCircuitBreakerCooldown = TimeSpan.FromMinutes(10),
        };
        config3.ClosedLoop.AutoStart.Should().BeTrue();
        config3.ClosedLoop.ClosedLoopPollInterval.Should().Be(TimeSpan.FromSeconds(120));
        config3.ClosedLoop.ClosedLoopMaxRunsPerCycle.Should().Be(5);
        config3.ClosedLoop.ClosedLoopMaxConsecutivePollFailures.Should().Be(8);
        config3.ClosedLoop.ClosedLoopMaxPagesToFetch.Should().Be(20);
        config3.ClosedLoop.ClosedLoopCircuitBreakerCooldown.Should().Be(TimeSpan.FromMinutes(10));

        // Agent sub-config
        var config4 = new PipelineConfiguration
        {
            BrainReadOnly = true,
            BrainPushMaxRetries = 5,
            AgentDisconnectGracePeriod = TimeSpan.FromMinutes(10),
            OutputBufferCapacity = 20_000,
        };
        config4.Agent.BrainReadOnly.Should().BeTrue();
        config4.Agent.BrainPushMaxRetries.Should().Be(5);
        config4.Agent.AgentDisconnectGracePeriod.Should().Be(TimeSpan.FromMinutes(10));
        config4.Agent.OutputBufferCapacity.Should().Be(20_000);

        // Commit sub-config
        var config5 = new PipelineConfiguration
        {
            BlacklistedPaths = new[] { "custom/path" },
        };
        config5.Commit.BlacklistedPaths.Should().BeEquivalentTo(new[] { "custom/path" });
    }
}
