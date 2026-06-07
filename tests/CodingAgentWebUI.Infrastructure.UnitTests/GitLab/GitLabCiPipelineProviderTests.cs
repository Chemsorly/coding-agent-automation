using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Pipeline.Models;
using NGitLab;

namespace CodingAgentWebUI.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based tests for GitLabCiPipelineProvider.
/// Feature: 029-gitlab-providers, Property 17.
/// </summary>
public class GitLabCiPipelineProviderTests
{
    #region Property 17: Pipeline status mapping

    /// <summary>
    /// Property 17: Pipeline status mapping — Pending statuses.
    /// All GitLab statuses that represent a "waiting" state map to PipelineRunState.Pending.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PendingJobStatusArbitrary)])]
    public void MapStatus_PendingStatuses_MapToPending(JobStatus status)
    {
        var result = GitLabCiPipelineProvider.MapStatus(status);

        result.Should().Be(PipelineRunState.Pending);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — Running status.
    /// The GitLab "Running" status maps to PipelineRunState.Running.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Fact]
    public void MapStatus_Running_MapsToRunning()
    {
        var result = GitLabCiPipelineProvider.MapStatus(JobStatus.Running);

        result.Should().Be(PipelineRunState.Running);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — Success status.
    /// The GitLab "Success" status maps to PipelineRunState.Passed.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Fact]
    public void MapStatus_Success_MapsToPassed()
    {
        var result = GitLabCiPipelineProvider.MapStatus(JobStatus.Success);

        result.Should().Be(PipelineRunState.Passed);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — Failed status.
    /// The GitLab "Failed" status maps to PipelineRunState.Failed.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Fact]
    public void MapStatus_Failed_MapsToFailed()
    {
        var result = GitLabCiPipelineProvider.MapStatus(JobStatus.Failed);

        result.Should().Be(PipelineRunState.Failed);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — Cancelled statuses.
    /// All GitLab statuses that represent cancellation map to PipelineRunState.Cancelled.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(CancelledJobStatusArbitrary)])]
    public void MapStatus_CancelledStatuses_MapToCancelled(JobStatus status)
    {
        var result = GitLabCiPipelineProvider.MapStatus(status);

        result.Should().Be(PipelineRunState.Cancelled);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — All defined JobStatus values map to a valid PipelineRunState.
    /// For any JobStatus value from the enum, MapStatus returns a defined PipelineRunState member.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(AllJobStatusArbitrary)])]
    public void MapStatus_AllDefinedStatuses_ReturnValidPipelineRunState(JobStatus status)
    {
        var result = GitLabCiPipelineProvider.MapStatus(status);

        result.Should().BeOneOf(
            PipelineRunState.Pending,
            PipelineRunState.Running,
            PipelineRunState.Passed,
            PipelineRunState.Failed,
            PipelineRunState.Cancelled);
    }

    /// <summary>
    /// Property 17: Pipeline status mapping — Mapping is deterministic.
    /// For any JobStatus value, calling MapStatus multiple times always returns the same result.
    /// **Validates: Requirements 14.4, 27.4**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(AllJobStatusArbitrary)])]
    public void MapStatus_IsDeterministic(JobStatus status)
    {
        var result1 = GitLabCiPipelineProvider.MapStatus(status);
        var result2 = GitLabCiPipelineProvider.MapStatus(status);

        result1.Should().Be(result2);
    }

    #endregion
}

#region Arbitraries

/// <summary>
/// Generates JobStatus values that should map to PipelineRunState.Pending.
/// Includes: Pending, WaitingForResource, Preparing, Created, Manual, Scheduled.
/// </summary>
public static class PendingJobStatusArbitrary
{
    public static Arbitrary<JobStatus> JobStatus()
    {
        var gen = Gen.Elements(
            NGitLab.JobStatus.Pending,
            NGitLab.JobStatus.WaitingForResource,
            NGitLab.JobStatus.Preparing,
            NGitLab.JobStatus.Created,
            NGitLab.JobStatus.Manual,
            NGitLab.JobStatus.Scheduled);
        return gen.ToArbitrary();
    }
}

/// <summary>
/// Generates JobStatus values that should map to PipelineRunState.Cancelled.
/// Includes: Canceled, Canceling, Skipped.
/// </summary>
public static class CancelledJobStatusArbitrary
{
    public static Arbitrary<JobStatus> JobStatus()
    {
        var gen = Gen.Elements(
            NGitLab.JobStatus.Canceled,
            NGitLab.JobStatus.Canceling,
            NGitLab.JobStatus.Skipped);
        return gen.ToArbitrary();
    }
}

/// <summary>
/// Generates all defined JobStatus enum values for exhaustive mapping tests.
/// </summary>
public static class AllJobStatusArbitrary
{
    public static Arbitrary<JobStatus> JobStatus()
    {
        var allValues = Enum.GetValues<NGitLab.JobStatus>();
        var gen = Gen.Elements(allValues);
        return gen.ToArbitrary();
    }
}

#endregion
