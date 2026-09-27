using AwesomeAssertions;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgent.Infrastructure.GitLab;
using CodingAgent.Pipeline.Models;
using Moq;
using NGitLab;
using NGitLab.Models;
using Serilog;

namespace CodingAgent.Infrastructure.UnitTests.GitLab;

/// <summary>
/// Property-based and characterization tests for GitLabCiPipelineProvider.
/// Feature: 029-gitlab-providers, Properties 17 and 18.
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

    #region Property 18: WaitForCompletionAsync characterization tests

    // These tests are characterization tests required by the issue prerequisite:
    // "Add characterization tests for both providers' WaitForCompletionAsync covering the
    // terminal-success, terminal-failure (with log enrichment), and timeout-fallback paths
    // before extracting."
    //
    // They use Moq to mock IGitLabClient/IPipelineClient/IJobClient at the interface level.
    // ExecuteWithResilienceAsync in GitLabProviderBase calls GetClientAsync(ct), which
    // returns the mock client passed to the internal test constructor directly.

    private const int TestProjectId = 42;
    private static readonly TimeSpan ShortPoll = TimeSpan.FromMilliseconds(20);
    private static readonly ILogger SilentLogger = new LoggerConfiguration().CreateLogger();
    // Sha1 requires exactly 40 hex characters
    private const string ValidSha = "abc123def456abc123def456abc123def456abc0";

    /// <summary>
    /// Creates a mock IGitLabClient that returns a single pipeline with the given status
    /// and optionally one job with the given job status and ID.
    /// </summary>
    private static Mock<IGitLabClient> CreateMockClient(
        JobStatus pipelineStatus,
        long pipelineId = 1,
        long jobId = 0,
        JobStatus jobStatus = JobStatus.Success,
        string? jobTrace = null)
    {
        var mockClient = new Mock<IGitLabClient>(MockBehavior.Loose);
        var mockPipelineClient = new Mock<IPipelineClient>(MockBehavior.Loose);
        var mockJobClientForLogs = new Mock<IJobClient>(MockBehavior.Loose);

        // PipelineBasic is a plain DTO with public settable properties
        var pipeline = new PipelineBasic
        {
            Status = pipelineStatus,
            Id = pipelineId,
            WebUrl = "https://gitlab.example.com/project/-/pipelines/1",
            CreatedAt = DateTime.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTime.UtcNow,
            Sha = new Sha1(ValidSha)
        };

        mockPipelineClient
            .Setup(p => p.Search(It.IsAny<PipelineQuery>()))
            .Returns(new[] { pipeline });

        // IPipelineClient.GetJobs(PipelineJobQuery) returns IEnumerable<Job>
        var jobs = jobId > 0
            ? new Job[]
            {
                new Job
                {
                    Id = jobId,
                    Name = "build",
                    Status = jobStatus,
                    WebUrl = "https://gitlab.example.com/project/-/jobs/" + jobId,
                    FailureReason = jobStatus == JobStatus.Failed ? "script_failure" : null
                }
            }
            : Array.Empty<Job>();

        mockPipelineClient
            .Setup(p => p.GetJobs(It.IsAny<PipelineJobQuery>()))
            .Returns(jobs);

        mockClient
            .Setup(c => c.GetPipelines(TestProjectId))
            .Returns(mockPipelineClient.Object);

        // IJobClient.GetTraceAsync path used by GetJobLogsAsync
        if (jobId > 0 && jobTrace != null)
        {
            mockJobClientForLogs
                .Setup(j => j.GetTraceAsync(jobId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(jobTrace);
        }
        else if (jobId > 0)
        {
            mockJobClientForLogs
                .Setup(j => j.GetTraceAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);
        }

        mockClient
            .Setup(c => c.GetJobs(TestProjectId))
            .Returns(mockJobClientForLogs.Object);

        return mockClient;
    }

    private static GitLabCiPipelineProvider CreateProvider(Mock<IGitLabClient> mockClient)
        => new(mockClient.Object, TestProjectId, ShortPoll, SilentLogger);

    /// <summary>
    /// Property 18: WaitForCompletionAsync — terminal success path.
    /// When the pipeline reports Success, WaitForCompletionAsync returns Passed without timeout.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_TerminalSuccess_ReturnsPassedStatus()
    {
        var mockClient = CreateMockClient(JobStatus.Success, pipelineId: 1, jobId: 10, jobStatus: JobStatus.Success);
        var provider = CreateProvider(mockClient);

        var result = await provider.WaitForCompletionAsync("main", "abc123sha", TimeSpan.FromSeconds(10), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
    }

    /// <summary>
    /// Property 18: WaitForCompletionAsync — terminal failure with log enrichment.
    /// When the pipeline fails and a job has a valid ID, WaitForCompletionAsync fetches
    /// job logs and returns them in the job's LogContent.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_TerminalFailure_EnrichesFailedJobsWithLogs()
    {
        const long failedJobId = 99;
        const string expectedTrace = "Error: compilation failed at line 12";

        var mockClient = CreateMockClient(
            JobStatus.Failed,
            pipelineId: 2,
            jobId: failedJobId,
            jobStatus: JobStatus.Failed,
            jobTrace: expectedTrace);
        var provider = CreateProvider(mockClient);

        // Use a generous timeout: the provider wraps synchronous NGitLab calls in Task.Run, and under
        // heavy parallel test load thread-pool pressure can delay those tasks. 60 s is far above any
        // realistic completion time while still failing the test if WaitForCompletionAsync truly hangs.
        var result = await provider.WaitForCompletionAsync("main", "sha-fail", TimeSpan.FromSeconds(60), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs.Should().HaveCount(1);
        result.Jobs[0].State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].LogContent.Should().Be(expectedTrace,
            "failed job log enrichment must fetch and inject the trace");
    }

    /// <summary>
    /// Property 18: WaitForCompletionAsync — timeout returns last observed status.
    /// When the pipeline never reaches a terminal state, the timeout fallback returns
    /// the last polled Running status.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_TimesOut_ReturnsLastStatus()
    {
        var mockClient = CreateMockClient(JobStatus.Running, pipelineId: 3, jobId: 0);
        var provider = CreateProvider(mockClient);

        var result = await provider.WaitForCompletionAsync(
            "main", "sha-running", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        // TODO: The BeOneOf assertion is weaker than needed. JobStatus.Running maps to
        // PipelineRunState.Running in the GitLab provider, so at least one poll should complete
        // before the 100ms timeout fires. Tighten this to result.State.Should().Be(PipelineRunState.Running)
        // to confirm the last polled value is returned rather than allowing an unconditional
        // Pending fallback to pass undetected.
        result.State.Should().BeOneOf(new[] { PipelineRunState.Running, PipelineRunState.Pending },
            "timeout must return the last polled state or Pending if no poll completed");
    }

    /// <summary>
    /// Property 18: WaitForCompletionAsync — timeout with no pipeline found returns Pending.
    /// When no pipeline exists for the branch/SHA, every poll returns Pending. On timeout
    /// the Pending status with the requested SHA is returned.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_NoPipeline_TimesOut_ReturnsPending()
    {
        var mockClient = new Mock<IGitLabClient>(MockBehavior.Loose);
        var mockPipelineClient = new Mock<IPipelineClient>(MockBehavior.Loose);

        // No pipelines found
        mockPipelineClient
            .Setup(p => p.Search(It.IsAny<PipelineQuery>()))
            .Returns(Array.Empty<PipelineBasic>());
        mockClient
            .Setup(c => c.GetPipelines(TestProjectId))
            .Returns(mockPipelineClient.Object);

        var provider = CreateProvider(mockClient);

        var result = await provider.WaitForCompletionAsync(
            "main", "sha-missing", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Pending);
    }

    /// <summary>
    /// Property 18: WaitForCompletionAsync — failed job with JobId=0 skips log fetch.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_FailedJobWithJobIdZero_DoesNotFetchLogs()
    {
        // JobId=0 — set up a failed pipeline whose job has id=0
        var mockClient = new Mock<IGitLabClient>(MockBehavior.Loose);
        var mockPipelineClient = new Mock<IPipelineClient>(MockBehavior.Loose);
        var mockJobClientForLogs = new Mock<IJobClient>(MockBehavior.Loose);

        var pipeline = new PipelineBasic
        {
            Status = JobStatus.Failed,
            Id = 5,
            WebUrl = "https://gitlab.example.com/-/pipelines/5",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Sha = new Sha1(ValidSha)
        };

        var jobWithZeroId = new Job
        {
            Id = 0,   // <-- zero: must be skipped
            Name = "test",
            Status = JobStatus.Failed,
            WebUrl = "https://gitlab.example.com/-/jobs/0"
        };

        mockPipelineClient.Setup(p => p.Search(It.IsAny<PipelineQuery>())).Returns(new[] { pipeline });
        mockPipelineClient.Setup(p => p.GetJobs(It.IsAny<PipelineJobQuery>())).Returns(new[] { jobWithZeroId });
        mockClient.Setup(c => c.GetPipelines(TestProjectId)).Returns(mockPipelineClient.Object);
        mockClient.Setup(c => c.GetJobs(TestProjectId)).Returns(mockJobClientForLogs.Object);

        var provider = CreateProvider(mockClient);

        var result = await provider.WaitForCompletionAsync("main", "sha-zero", TimeSpan.FromSeconds(5), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].LogContent.Should().BeNull("JobId=0 must be skipped during log enrichment");
        mockJobClientForLogs.Verify(j => j.GetTraceAsync(It.IsAny<long>(), It.IsAny<CancellationToken>()), Times.Never,
            "GetTraceAsync must not be called for a job with ID=0");
    }

    /// <summary>
    /// Property 18: WaitForCompletionAsync — failed job log fetch returns null; LogContent stays null.
    /// </summary>
    [Fact]
    public async Task WaitForCompletion_FailedJobLogFetchFails_JobHasNullLogContent()
    {
        const long failedJobId = 77;

        var mockClient = CreateMockClient(
            JobStatus.Failed,
            pipelineId: 6,
            jobId: failedJobId,
            jobStatus: JobStatus.Failed,
            jobTrace: null);   // <-- fetch returns null
        var provider = CreateProvider(mockClient);

        var result = await provider.WaitForCompletionAsync("main", "sha-nolog", TimeSpan.FromSeconds(5), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].LogContent.Should().BeNull(
            "when GetTraceAsync returns null, LogContent must remain null and not throw");
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
