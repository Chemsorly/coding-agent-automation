using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

/// <summary>
/// Unit tests for <see cref="PendingJob"/> — verifies that <see cref="PendingJob.IsConsolidation"/>
/// uses <see cref="PipelineRunType"/> as the single reliable discriminator.
/// </summary>
public class PendingJobTests
{
    // ── IsConsolidation ──────────────────────────────────────────────────────

    [Fact]
    public void IsConsolidation_WhenRunTypeIsConsolidation_ReturnsTrue()
    {
        var job = BuildJob(PipelineRunType.Consolidation);
        job.IsConsolidation.Should().BeTrue(
            because: "RunType == PipelineRunType.Consolidation is the single discriminator for consolidation jobs");
    }

    [Fact]
    public void IsConsolidation_WhenRunTypeIsImplementation_ReturnsFalse()
    {
        var job = BuildJob(PipelineRunType.Implementation);
        job.IsConsolidation.Should().BeFalse(
            because: "Implementation jobs are not consolidation jobs");
    }

    [Fact]
    public void IsConsolidation_WhenRunTypeIsReview_ReturnsFalse()
    {
        var job = BuildJob(PipelineRunType.Review);
        job.IsConsolidation.Should().BeFalse(
            because: "Review jobs are not consolidation jobs");
    }

    [Fact]
    public void IsConsolidation_WhenRunTypeIsDecompositionAnalysis_ReturnsFalse()
    {
        var job = BuildJob(PipelineRunType.DecompositionAnalysis);
        job.IsConsolidation.Should().BeFalse();
    }

    [Fact]
    public void IsConsolidation_WhenRunTypeIsDecomposition_ReturnsFalse()
    {
        var job = BuildJob(PipelineRunType.Decomposition);
        job.IsConsolidation.Should().BeFalse();
    }

    /// <summary>
    /// Verifies that IsConsolidation is based solely on RunType, not on ConsolidationRunType.HasValue.
    /// A consolidation job with a missing ConsolidationRunType (e.g. failed payload extraction) must
    /// still be identified as a consolidation job, because RunType was set correctly by ResolveRunType.
    /// </summary>
    [Fact]
    public void IsConsolidation_WhenRunTypeIsConsolidation_AndConsolidationRunTypeIsNull_StillReturnsTrue()
    {
        var job = new PendingJob
        {
            IssueIdentifier = "consolidation-run-123",
            IssueProviderId = "consolidation",
            RepoProviderId = "consolidation",
            InitiatedBy = "loop",
            EnqueuedAt = DateTimeOffset.UtcNow,
            RunType = PipelineRunType.Consolidation,
            TaskType = WorkItemTaskType.Consolidation,
            ConsolidationRunType = null  // null — payload extraction may have failed
        };

        job.IsConsolidation.Should().BeTrue(
            because: "RunType is the authoritative discriminator; ConsolidationRunType is a sub-classification and its absence must not affect IsConsolidation");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    // TODO: [WARNING] The IsConsolidation tests cover all five PipelineRunType values that currently exist,
    // but there is no catch-all parameterised negative-case test covering all non-Consolidation enum members.
    // If the implementation is inadvertently widened (e.g., reverted to a multi-discriminator expression),
    // a newly added enum value would not be caught. Consider a [MemberData] / [ClassData] test that
    // enumerates all Enum.GetValues<PipelineRunType>() except Consolidation and asserts IsConsolidation == false.

    private static PendingJob BuildJob(PipelineRunType runType) => new()
    {
        IssueIdentifier = "org/repo#1",
        IssueProviderId = "ip-1",
        RepoProviderId = "rp-1",
        InitiatedBy = "test",
        EnqueuedAt = DateTimeOffset.UtcNow,
        RunType = runType
    };
}
