using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;
using CodingAgentWebUI.Pipeline.Models;
using MessagePack;
using MessagePack.Resolvers;

namespace CodingAgentWebUI.Pipeline.UnitTests.Serialization;

/// <summary>
/// Binary characterization test for <see cref="PipelineConfiguration"/> MessagePack serialization.
/// Proves that the serialized binary output is identical for a fully-populated configuration —
/// guards against accidental wire format changes from refactoring.
/// </summary>
public class PipelineConfigurationMessagePackBinaryTests
{
    private static readonly MessagePackSerializerOptions MsgPackOptions =
        ContractlessStandardResolverAllowPrivate.Options;

    /// <summary>
    /// Serialize a fully-populated PipelineConfiguration and verify that the deserialized
    /// output is byte-for-byte identical when re-serialized. This proves that the serialization
    /// surface is stable and that no hidden state (e.g., sub-config objects) affects the wire format.
    /// </summary>
    [Fact]
    public void MessagePackBinaryRoundTrip_FullyPopulatedConfig_ProducesIdenticalBytes()
    {
        var config = CreateFullyPopulatedConfig();

        // Serialize → deserialize → re-serialize
        var bytes1 = MessagePackSerializer.Serialize(config, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(bytes1, MsgPackOptions);
        var bytes2 = MessagePackSerializer.Serialize(deserialized, MsgPackOptions);

        // Binary output must be identical
        bytes2.Should().BeEquivalentTo(bytes1,
            "re-serializing a deserialized PipelineConfiguration must produce identical binary output");
    }

    /// <summary>
    /// Verify that default-constructed PipelineConfiguration produces stable binary output.
    /// </summary>
    [Fact]
    public void MessagePackBinaryRoundTrip_DefaultConfig_ProducesIdenticalBytes()
    {
        var config = new PipelineConfiguration();

        var bytes1 = MessagePackSerializer.Serialize(config, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(bytes1, MsgPackOptions);
        var bytes2 = MessagePackSerializer.Serialize(deserialized, MsgPackOptions);

        bytes2.Should().BeEquivalentTo(bytes1);
    }

    /// <summary>
    /// Verify that all 63 [Key]-annotated properties survive MessagePack round-trip with correct values.
    /// </summary>
    [Fact]
    public void MessagePackRoundTrip_FullyPopulatedConfig_PreservesAllPropertyValues()
    {
        var config = CreateFullyPopulatedConfig();

        var bytes = MessagePackSerializer.Serialize(config, MsgPackOptions);
        var deserialized = MessagePackSerializer.Deserialize<PipelineConfiguration>(bytes, MsgPackOptions);

        deserialized.Should().NotBeNull();
        deserialized!.MaxRetries.Should().Be(7);
        deserialized.MaxAnalysisRetries.Should().Be(4);
        deserialized.IssuePageSize.Should().Be(50);
        deserialized.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        deserialized.WorkspaceBaseDirectory.Should().Be("/custom/workspaces");
        deserialized.AnalysisPrompt.Should().Be("Custom analysis prompt");
        deserialized.ImplementationPrompt.Should().Be("Custom implementation prompt");
        deserialized.AnalysisReviewEnabled.Should().BeFalse();
        deserialized.AnalysisReviewPrompt.Should().Be("Custom review prompt");
        deserialized.AnalysisRefinementPrompt.Should().Be("Custom refinement prompt");
        deserialized.AcceptanceCriteriaEnabled.Should().BeFalse();
        deserialized.AcceptanceCriteriaPrompt.Should().Be("Custom AC prompt");
        deserialized.RefactoringReviewEnabled.Should().BeFalse();
        deserialized.BrainConsolidationReviewEnabled.Should().BeFalse();
        deserialized.HarnessSuggestionsReviewEnabled.Should().BeFalse();
        deserialized.BaselineHealthCheckEnabled.Should().BeFalse();
        deserialized.ExternalCiTimeout.Should().Be(TimeSpan.FromMinutes(25));
        deserialized.ExternalCiPollInterval.Should().Be(TimeSpan.FromSeconds(45));
        deserialized.MaxInfrastructureRetries.Should().Be(4);
        deserialized.CiNotStartedTimeout.Should().Be(TimeSpan.FromMinutes(8));
        deserialized.CiNotStartedMaxRetries.Should().Be(10);
        deserialized.StallWarningInterval.Should().Be(TimeSpan.FromMinutes(5));
        deserialized.StallPollInterval.Should().Be(TimeSpan.FromSeconds(15));
        deserialized.BlacklistedPaths.Should().BeEquivalentTo(new[] { "custom/path", "another/path" });
        deserialized.PipelineInjectedPaths.Should().BeEquivalentTo(new[] { ".injected" });
        deserialized.AnalysisCommitThreshold.Should().Be(50);
        deserialized.FailedWorkspaceRetentionDays.Should().Be(14);
        deserialized.BrainReadOnly.Should().BeTrue();
        deserialized.ClosedLoopAutoStart.Should().BeTrue();
        deserialized.ClosedLoopPollInterval.Should().Be(TimeSpan.FromSeconds(120));
        deserialized.ClosedLoopMaxRunsPerCycle.Should().Be(5);
        deserialized.ClosedLoopMaxConsecutivePollFailures.Should().Be(10);
        deserialized.ClosedLoopMaxBackoffInterval.Should().Be(TimeSpan.FromMinutes(30));
        deserialized.ClosedLoopMaxPagesToFetch.Should().Be(20);
        deserialized.ClosedLoopCircuitBreakerCooldown.Should().Be(TimeSpan.FromMinutes(10));
        deserialized.DefaultRequiredAgentLabels.Should().Be("kiro,dotnet");
        deserialized.BrainPushMaxRetries.Should().Be(5);
        deserialized.AgentDisconnectGracePeriod.Should().Be(TimeSpan.FromMinutes(10));
        deserialized.AgentBusyProgressTimeout.Should().Be(TimeSpan.FromMinutes(90));
        deserialized.OutputBufferCapacity.Should().Be(20_000);
        deserialized.OutputLinesCapacity.Should().Be(8_000);
        deserialized.ChatHistoryCapacity.Should().Be(300);
        deserialized.QualityGateHistoryCapacity.Should().Be(75);
        deserialized.RetryErrorsCapacity.Should().Be(150);
        deserialized.HeartbeatSweepIntervalSeconds.Should().Be(90);
        deserialized.HeartbeatTimeoutSeconds.Should().Be(120);
        deserialized.OrphanedLabelSweepIntervalMinutes.Should().Be(60);
        deserialized.MaxRefactoringProposals.Should().Be(5);
        deserialized.HotspotAnalysisLookback.Should().Be(TimeSpan.FromDays(180));
        deserialized.MaxDecompositionSubIssues.Should().Be(15);
        deserialized.MaxConcurrentDecompositions.Should().Be(4);
        deserialized.DecompositionTimeout.Should().Be(TimeSpan.FromMinutes(30));
        deserialized.MaxOpenIssuesForContext.Should().Be(100);
        deserialized.RefactoringOutcomeLookback.Should().Be(TimeSpan.FromDays(180));
        deserialized.MaxIssueImages.Should().Be(20);
        deserialized.MaxImageSizeBytes.Should().Be(10_485_760);
        deserialized.MaxTotalImageSizeBytes.Should().Be(41_943_040);
        deserialized.TotalImageDownloadTimeoutSeconds.Should().Be(120);
        deserialized.EnableIssueImageExtraction.Should().BeFalse();
        deserialized.EnableNativeImageParts.Should().BeFalse();
        deserialized.ImageDownloadTimeoutSeconds.Should().Be(60);
    }

    private static PipelineConfiguration CreateFullyPopulatedConfig() => new()
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
}
