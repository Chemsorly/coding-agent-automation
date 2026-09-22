using Octokit;
using Polly;
using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Infrastructure.GitHub;

/// <summary>
/// Reads GitHub Actions workflow run status via the GitHub REST API.
/// Supports both static token and dynamic token provider (GitHub App auth).
/// </summary>
public class GitHubActionsPipelineProvider : GitHubProviderBase, IPipelineProvider
{
    private readonly TimeSpan _pollInterval;
    private readonly Serilog.ILogger _logger;
    private readonly ResiliencePipeline _logsPipeline;

    /// <inheritdoc />
    public PipelineProviderType ProviderType => PipelineProviderType.GitHubActions;

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// </summary>
    public GitHubActionsPipelineProvider(
        GitHubConnectionInfo connection,
        Func<CancellationToken, Task<string>> tokenProvider,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
        : base(connection, tokenProvider)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
        _logsPipeline = ResiliencePipelineFactory.CreateGitHubActionsLogsPipeline(_logger);
    }

    /// <summary>
    /// Creates a provider with a static token.
    /// </summary>
    public GitHubActionsPipelineProvider(
        GitHubConnectionInfo connection,
        string token,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
        : base(connection, token)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
        _logsPipeline = ResiliencePipelineFactory.CreateGitHubActionsLogsPipeline(_logger);
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubActionsPipelineProvider(
        GitHubConnectionInfo connection,
        IGitHubClient client,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
        : base(connection, client)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
        _logsPipeline = ResiliencePipelineFactory.CreateGitHubActionsLogsPipeline(_logger);
    }

    public async Task<PipelineRunStatus> GetRunStatusAsync(
        string branchName, string? commitSha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        // Pass head_sha as a server-side filter when a specific SHA is requested.
        // Previously this was a client-side Where() on the default page of results (30 runs),
        // which caused the SHA to become invisible once enough re-trigger commits pushed it off
        // the first page — making every subsequent poll return Pending even though CI had run.
        var request = commitSha != null
            ? new WorkflowRunsRequest { Branch = branchName, HeadSha = commitSha }
            : new WorkflowRunsRequest { Branch = branchName };
        var runs = await ExecuteWithResilienceAsync(
            client => client.Actions.Workflows.Runs.List(Owner, Repo, request),
            "GetRunStatus.ListRuns", ct);

        var matchingRuns = runs.WorkflowRuns.ToList();

        if (matchingRuns.Count == 0)
        {
            return new PipelineRunStatus
            {
                State = PipelineRunState.Pending,
                Jobs = Array.Empty<PipelineJobResult>(),
                CommitSha = commitSha
            };
        }

        var jobs = new List<PipelineJobResult>();
        foreach (var run in matchingRuns)
        {
            var runJobs = await ExecuteWithResilienceAsync(
                client => client.Actions.Workflows.Jobs.List(Owner, Repo, run.Id),
                "GetRunStatus.ListJobs", ct);
            foreach (var job in runJobs.Jobs)
            {
                jobs.Add(new PipelineJobResult
                {
                    Name = job.Name,
                    State = MapJobState(job.Status.Value, job.Conclusion?.Value),
                    FailureReason = job.Conclusion?.Value == WorkflowJobConclusion.Failure
                        ? $"Job '{job.Name}' failed"
                        : null,
                    LogUrl = job.HtmlUrl,
                    JobId = job.Id
                });
            }
        }

        var aggregateState = AggregateState(matchingRuns);
        var firstRun = matchingRuns.OrderBy(r => r.CreatedAt).First();
        var lastRun = matchingRuns.OrderByDescending(r => r.UpdatedAt).First();

        return new PipelineRunStatus
        {
            State = aggregateState,
            Jobs = jobs,
            Url = firstRun.HtmlUrl,
            StartedAt = firstRun.CreatedAt.UtcDateTime,
            CompletedAt = aggregateState is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled
                ? lastRun.UpdatedAt.UtcDateTime
                : null,
            CommitSha = commitSha ?? firstRun.HeadSha
        };
    }

    public Task<PipelineRunStatus> WaitForCompletionAsync(
        string branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        _logger.Information("Polling CI for branch {Branch} (commit: {CommitSha}, timeout: {Timeout})",
            branchName, commitSha ?? "any", timeout);

        return PipelinePollingHelper.PollUntilCompleteAsync(
            getRunStatusAsync: ct2 => GetRunStatusAsync(branchName, commitSha, ct2),
            enrichFailedJobsAsync: (status, ct2) => PipelinePollingHelper.EnrichFailedJobsWithLogsAsync(
                status, GetJobLogsAsync, "job", ct2, _logger),
            isTerminalState: s => s.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled,
            pollInterval: _pollInterval,
            timeout: timeout,
            logPrefix: "CI",
            ct: ct,
            logger: _logger);
    }

    /// <inheritdoc />
    public async Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct)
    {
        try
        {
            var rawLog = await _logsPipeline.ExecuteAsync(async token =>
            {
                var client = await GetClientAsync(token);
                return await client.Actions.Workflows.Jobs.GetLogs(Owner, Repo, jobId);
            }, ct);
            return string.IsNullOrEmpty(rawLog) ? null : rawLog;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch logs for job (id={JobId})", jobId);
            return null;
        }
    }

    internal static PipelineRunState MapJobState(WorkflowJobStatus status, WorkflowJobConclusion? conclusion)
    {
        if (status == WorkflowJobStatus.Queued) return PipelineRunState.Pending;
        if (status == WorkflowJobStatus.InProgress) return PipelineRunState.Running;

        return conclusion switch
        {
            WorkflowJobConclusion.Success => PipelineRunState.Passed,
            WorkflowJobConclusion.Failure => PipelineRunState.Failed,
            WorkflowJobConclusion.Cancelled => PipelineRunState.Cancelled,
            _ => PipelineRunState.Failed
        };
    }

    /// <summary>
    /// Aggregates multiple workflow run statuses into a single pipeline state.
    /// Uses early-return semantics: if any run has already failed or been cancelled,
    /// returns immediately without waiting for other in-progress runs to complete.
    /// This gives the agent faster feedback — it can start fixing the failure while
    /// other workflows are still running. The next push will re-trigger all workflows.
    /// </summary>
    internal static PipelineRunState AggregateState(IReadOnlyList<WorkflowRun> runs)
    {
        if (runs.Count == 0) return PipelineRunState.Pending;

        var hasRunning = false;
        var hasPending = false;
        var hasFailed = false;
        var hasCancelled = false;

        foreach (var run in runs)
        {
            var state = ClassifyRun(run);
            if (state == PipelineRunState.Running)       hasRunning = true;
            else if (state == PipelineRunState.Pending)  hasPending = true;
            else if (state == PipelineRunState.Failed)   hasFailed = true;
            else if (state == PipelineRunState.Cancelled) hasCancelled = true;
        }

        // Early-return: surface failures immediately even if other runs are still in progress.
        // The agent benefits from faster feedback; the next push re-triggers all workflows.
        if (hasFailed) return PipelineRunState.Failed;
        if (hasCancelled) return PipelineRunState.Cancelled;
        if (hasRunning) return PipelineRunState.Running;
        if (hasPending) return PipelineRunState.Pending;
        return PipelineRunState.Passed;
    }

    /// <summary>
    /// Classifies a single workflow run into a <see cref="PipelineRunState"/>.
    /// Returns <c>null</c>-equivalent (Passed) for completed runs with a success conclusion.
    /// </summary>
    private static PipelineRunState ClassifyRun(WorkflowRun run)
    {
        if (run.Status.Value is WorkflowRunStatus.InProgress or WorkflowRunStatus.Waiting)
            return PipelineRunState.Running;

        if (run.Status.Value is WorkflowRunStatus.Queued or WorkflowRunStatus.Requested or WorkflowRunStatus.Pending)
            return PipelineRunState.Pending;

        if (run.Status.Value == WorkflowRunStatus.Completed)
        {
            if (run.Conclusion?.Value == WorkflowRunConclusion.Failure)  return PipelineRunState.Failed;
            if (run.Conclusion?.Value == WorkflowRunConclusion.Cancelled) return PipelineRunState.Cancelled;
        }

        return PipelineRunState.Passed;
    }
}
