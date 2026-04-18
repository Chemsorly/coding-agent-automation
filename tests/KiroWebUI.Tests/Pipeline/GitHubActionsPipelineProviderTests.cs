using AwesomeAssertions;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;
using Moq;
using Octokit;

namespace KiroWebUI.Tests.Pipeline;

public class GitHubActionsPipelineProviderTests
{
    private readonly Mock<IGitHubClient> _mockClient;
    private readonly Mock<IActionsClient> _mockActions;
    private readonly Mock<IActionsWorkflowsClient> _mockWorkflows;
    private readonly Mock<IActionsWorkflowRunsClient> _mockRuns;
    private readonly Mock<IActionsWorkflowJobsClient> _mockJobs;
    private readonly GitHubActionsPipelineProvider _provider;

    public GitHubActionsPipelineProviderTests()
    {
        _mockClient = new Mock<IGitHubClient>();
        _mockActions = new Mock<IActionsClient>();
        _mockWorkflows = new Mock<IActionsWorkflowsClient>();
        _mockRuns = new Mock<IActionsWorkflowRunsClient>();
        _mockJobs = new Mock<IActionsWorkflowJobsClient>();

        _mockClient.Setup(c => c.Actions).Returns(_mockActions.Object);
        _mockActions.Setup(a => a.Workflows).Returns(_mockWorkflows.Object);
        _mockWorkflows.Setup(w => w.Runs).Returns(_mockRuns.Object);
        _mockWorkflows.Setup(w => w.Jobs).Returns(_mockJobs.Object);

        _provider = new GitHubActionsPipelineProvider(
            _mockClient.Object, "owner", "repo", TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task GetRunStatus_NoRuns_ReturnsPending()
    {
        SetupWorkflowRuns(Array.Empty<WorkflowRun>());

        var result = await _provider.GetRunStatusAsync("main", null, CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Pending);
        result.Jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRunStatus_AllPassed_ReturnsPassed()
    {
        var run = CreateWorkflowRun(1, "abc123", WorkflowRunStatus.Completed, WorkflowRunConclusion.Success);
        SetupWorkflowRuns(new[] { run });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.Completed, WorkflowJobConclusion.Success) });

        var result = await _provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
        result.Jobs.Should().HaveCount(1);
        result.Jobs[0].Name.Should().Be("build");
        result.Jobs[0].State.Should().Be(PipelineRunState.Passed);
        result.CommitSha.Should().Be("abc123");
    }

    [Fact]
    public async Task GetRunStatus_OneFailed_ReturnsFailed()
    {
        var run = CreateWorkflowRun(1, "abc123", WorkflowRunStatus.Completed, WorkflowRunConclusion.Failure);
        SetupWorkflowRuns(new[] { run });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.Completed, WorkflowJobConclusion.Failure) });

        var result = await _provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].State.Should().Be(PipelineRunState.Failed);
        result.Jobs[0].FailureReason.Should().Contain("build");
    }

    [Fact]
    public async Task GetRunStatus_InProgress_ReturnsRunning()
    {
        var run = CreateWorkflowRun(1, "abc123", WorkflowRunStatus.InProgress, null);
        SetupWorkflowRuns(new[] { run });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.InProgress, null) });

        var result = await _provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Running);
    }

    [Fact]
    public async Task GetRunStatus_FiltersByCommitSha()
    {
        var run1 = CreateWorkflowRun(1, "abc123", WorkflowRunStatus.Completed, WorkflowRunConclusion.Success);
        var run2 = CreateWorkflowRun(2, "def456", WorkflowRunStatus.Completed, WorkflowRunConclusion.Failure);
        SetupWorkflowRuns(new[] { run1, run2 });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.Completed, WorkflowJobConclusion.Success) });

        var result = await _provider.GetRunStatusAsync("main", "abc123", CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
    }

    [Fact]
    public async Task WaitForCompletion_ReturnsWhenComplete()
    {
        var callCount = 0;
        _mockRuns.Setup(r => r.List("owner", "repo", It.IsAny<WorkflowRunsRequest>()))
            .ReturnsAsync(() =>
            {
                callCount++;
                var status = callCount < 3 ? WorkflowRunStatus.InProgress : WorkflowRunStatus.Completed;
                var conclusion = callCount < 3 ? (WorkflowRunConclusion?)null : WorkflowRunConclusion.Success;
                return new WorkflowRunsResponse(1, new[] { CreateWorkflowRun(1, "abc", status, conclusion) });
            });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.Completed, WorkflowJobConclusion.Success) });

        var result = await _provider.WaitForCompletionAsync("main", "abc", TimeSpan.FromSeconds(10), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Passed);
        callCount.Should().BeGreaterThanOrEqualTo(3);
    }

    [Fact]
    public async Task WaitForCompletion_TimesOut_ReturnsLastStatus()
    {
        var run = CreateWorkflowRun(1, "abc", WorkflowRunStatus.InProgress, null);
        SetupWorkflowRuns(new[] { run });
        SetupJobs(1, new[] { CreateJob("build", WorkflowJobStatus.InProgress, null) });

        var result = await _provider.WaitForCompletionAsync("main", "abc", TimeSpan.FromMilliseconds(100), CancellationToken.None);

        result.State.Should().Be(PipelineRunState.Running);
    }

    [Theory]
    [InlineData(WorkflowJobStatus.Queued, null, PipelineRunState.Pending)]
    [InlineData(WorkflowJobStatus.InProgress, null, PipelineRunState.Running)]
    [InlineData(WorkflowJobStatus.Completed, WorkflowJobConclusion.Success, PipelineRunState.Passed)]
    [InlineData(WorkflowJobStatus.Completed, WorkflowJobConclusion.Failure, PipelineRunState.Failed)]
    [InlineData(WorkflowJobStatus.Completed, WorkflowJobConclusion.Cancelled, PipelineRunState.Cancelled)]
    public void MapJobState_MapsCorrectly(WorkflowJobStatus status, WorkflowJobConclusion? conclusion, PipelineRunState expected)
    {
        GitHubActionsPipelineProvider.MapJobState(status, conclusion).Should().Be(expected);
    }

    // --- Helpers ---

    private void SetupWorkflowRuns(IReadOnlyList<WorkflowRun> runs)
    {
        _mockRuns.Setup(r => r.List("owner", "repo", It.IsAny<WorkflowRunsRequest>()))
            .ReturnsAsync(new WorkflowRunsResponse(runs.Count, runs));
    }

    private void SetupJobs(long runId, IReadOnlyList<WorkflowJob> jobs)
    {
        _mockJobs.Setup(j => j.List("owner", "repo", runId))
            .ReturnsAsync(new WorkflowJobsResponse(jobs.Count, jobs));
    }

    private static WorkflowRun CreateWorkflowRun(long id, string headSha, WorkflowRunStatus status, WorkflowRunConclusion? conclusion)
    {
        return new WorkflowRun(
            id: id,
            name: "CI",
            nodeId: "node",
            checkSuiteId: 0,
            checkSuiteNodeId: "node",
            headBranch: "main",
            headSha: headSha,
            path: ".github/workflows/ci.yml",
            runNumber: 1,
            @event: "push",
            displayTitle: "CI",
            status: status,
            conclusion: conclusion,
            workflowId: 1,
            url: $"https://api.github.com/repos/owner/repo/actions/runs/{id}",
            htmlUrl: $"https://github.com/owner/repo/actions/runs/{id}",
            pullRequests: Array.Empty<PullRequest>(),
            createdAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt: DateTimeOffset.UtcNow,
            actor: null,
            runAttempt: 1,
            referencedWorkflows: null,
            runStartedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            triggeringActor: null,
            jobsUrl: "",
            logsUrl: "",
            checkSuiteUrl: "",
            artifactsUrl: "",
            cancelUrl: "",
            rerunUrl: "",
            previousAttemptUrl: null,
            workflowUrl: "",
            headCommit: null,
            repository: null,
            headRepository: null,
            headRepositoryId: 0);
    }

    private static WorkflowJob CreateJob(string name, WorkflowJobStatus status, WorkflowJobConclusion? conclusion)
    {
        return new WorkflowJob(
            id: 1,
            runId: 1,
            runUrl: "",
            nodeId: "node",
            headSha: "abc",
            url: "",
            htmlUrl: $"https://github.com/owner/repo/actions/runs/1/job/1",
            status: status,
            conclusion: conclusion,
            createdAt: DateTimeOffset.UtcNow,
            startedAt: DateTimeOffset.UtcNow,
            completedAt: status == WorkflowJobStatus.Completed ? DateTimeOffset.UtcNow : null,
            name: name,
            steps: null,
            checkRunUrl: "",
            labels: new[] { "ubuntu-latest" },
            runnerId: 1,
            runnerName: "runner",
            runnerGroupId: 1,
            runnerGroupName: "default");
    }
}
