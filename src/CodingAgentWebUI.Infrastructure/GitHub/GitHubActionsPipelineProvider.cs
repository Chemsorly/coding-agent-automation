using Octokit;
using Polly;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.GitHub;

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

        var request = new WorkflowRunsRequest { Branch = branchName };
        var runs = await ExecuteWithResilienceAsync(
            client => client.Actions.Workflows.Runs.List(Owner, Repo, request),
            "GetRunStatus.ListRuns", ct);

        // Filter by commit SHA if provided
        var matchingRuns = commitSha != null
            ? runs.WorkflowRuns.Where(r => r.HeadSha == commitSha).ToList()
            : runs.WorkflowRuns.ToList();

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

    public async Task<PipelineRunStatus> WaitForCompletionAsync(
        string branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        _logger.Information("Polling CI for branch {Branch} (commit: {CommitSha}, timeout: {Timeout})",
            branchName, commitSha ?? "any", timeout);

        var pollCount = 0;
        PipelineRunStatus? lastStatus = null;

        return await TimeoutHelper.ExecuteWithTimeoutAsync(
            timeout, ct,
            async linkedCt =>
            {
                while (true)
                {
                    linkedCt.ThrowIfCancellationRequested();
                    pollCount++;

                    var status = await GetRunStatusAsync(branchName, commitSha, linkedCt);
                    lastStatus = status;

                    _logger.Information("CI poll #{PollCount}: {State} — {RunCount} run(s), {JobCount} job(s)",
                        pollCount, status.State, status.Jobs.Count > 0 ? status.Jobs.Count : 0,
                        status.Jobs.Count);

                    if (status.State is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled)
                    {
                        _logger.Information("CI completed: {State} after {PollCount} poll(s)", status.State, pollCount);

                        if (status.State == PipelineRunState.Failed)
                        {
                            status = await EnrichFailedJobsWithLogsAsync(status, linkedCt);
                        }

                        return status;
                    }

                    await Task.Delay(_pollInterval, linkedCt);
                }
            },
            () =>
            {
                _logger.Warning("CI polling timed out after {Timeout} ({PollCount} polls). Last state: {State}",
                    timeout, pollCount, lastStatus?.State);
                return Task.FromResult(lastStatus ?? new PipelineRunStatus
                {
                    State = PipelineRunState.Pending,
                    Jobs = Array.Empty<PipelineJobResult>(),
                    CommitSha = commitSha
                });
            });
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

    /// <summary>
    /// Fetches full log content from the GitHub Actions API for each failed job.
    /// </summary>
    private async Task<PipelineRunStatus> EnrichFailedJobsWithLogsAsync(
        PipelineRunStatus status, CancellationToken ct)
    {
        var failedJobIds = status.Jobs
            .Where(j => j.State == PipelineRunState.Failed && j.JobId > 0)
            .Select(j => j.JobId)
            .ToHashSet();

        if (failedJobIds.Count == 0)
            return status;

        var logsByJobId = await FetchLogsForFailedJobsAsync(failedJobIds, ct);

        if (logsByJobId.Count == 0)
            return status;

        return new PipelineRunStatus
        {
            State = status.State,
            Jobs = BuildEnrichedJobList(status.Jobs, logsByJobId),
            Url = status.Url,
            StartedAt = status.StartedAt,
            CompletedAt = status.CompletedAt,
            CommitSha = status.CommitSha
        };
    }

    /// <summary>
    /// Fetches log content for a set of failed job IDs. Returns a dictionary mapping
    /// job ID to log content for each job where logs were successfully retrieved.
    /// </summary>
    private async Task<Dictionary<long, string>> FetchLogsForFailedJobsAsync(
        IEnumerable<long> failedJobIds, CancellationToken ct)
    {
        var logsByJobId = new Dictionary<long, string>();
        foreach (var jobId in failedJobIds)
        {
            var logContent = await GetJobLogsAsync(jobId, ct);
            if (logContent is not null)
            {
                logsByJobId[jobId] = logContent;
                _logger.Debug("Fetched {Length} chars of logs for failed job (id={JobId})",
                    logContent.Length, jobId);
            }
        }
        return logsByJobId;
    }

    /// <summary>
    /// Produces a new job list with log content injected for jobs whose IDs appear in
    /// <paramref name="logsByJobId"/>. Jobs not in the dictionary are returned unchanged.
    /// </summary>
    private static List<PipelineJobResult> BuildEnrichedJobList(
        IReadOnlyList<PipelineJobResult> originalJobs,
        Dictionary<long, string> logsByJobId)
    {
        return originalJobs.Select(job =>
        {
            if (logsByJobId.TryGetValue(job.JobId, out var content))
            {
                return new PipelineJobResult
                {
                    Name = job.Name,
                    State = job.State,
                    FailureReason = job.FailureReason,
                    LogUrl = job.LogUrl,
                    JobId = job.JobId,
                    LogContent = content
                };
            }
            return job;
        }).ToList();
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
