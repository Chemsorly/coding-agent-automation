using Octokit;
using Polly;
using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

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

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        var linkedCt = timeoutCts.Token;
        var pollCount = 0;
        PipelineRunStatus? lastStatus = null;

        while (true)
        {
            try
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
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.Warning("CI polling timed out after {Timeout} ({PollCount} polls). Last state: {State}",
                    timeout, pollCount, lastStatus?.State);
                return lastStatus ?? new PipelineRunStatus
                {
                    State = PipelineRunState.Pending,
                    Jobs = Array.Empty<PipelineJobResult>(),
                    CommitSha = commitSha
                };
            }
        }
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

        if (logsByJobId.Count == 0)
            return status;

        var enrichedJobs = status.Jobs.Select(job =>
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

        return new PipelineRunStatus
        {
            State = status.State,
            Jobs = enrichedJobs,
            Url = status.Url,
            StartedAt = status.StartedAt,
            CompletedAt = status.CompletedAt,
            CommitSha = status.CommitSha
        };
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

    internal static PipelineRunState AggregateState(IReadOnlyList<WorkflowRun> runs)
    {
        if (runs.Count == 0) return PipelineRunState.Pending;

        var hasRunning = false;
        var hasPending = false;
        var hasFailed = false;
        var hasCancelled = false;

        foreach (var run in runs)
        {
            if (run.Status.Value == WorkflowRunStatus.InProgress || run.Status.Value == WorkflowRunStatus.Waiting)
                hasRunning = true;
            else if (run.Status.Value == WorkflowRunStatus.Queued || run.Status.Value == WorkflowRunStatus.Requested || run.Status.Value == WorkflowRunStatus.Pending)
                hasPending = true;
            else if (run.Status.Value == WorkflowRunStatus.Completed)
            {
                if (run.Conclusion?.Value == WorkflowRunConclusion.Failure)
                    hasFailed = true;
                else if (run.Conclusion?.Value == WorkflowRunConclusion.Cancelled)
                    hasCancelled = true;
            }
        }

        if (hasRunning) return PipelineRunState.Running;
        if (hasPending) return PipelineRunState.Pending;
        if (hasFailed) return PipelineRunState.Failed;
        if (hasCancelled) return PipelineRunState.Cancelled;
        return PipelineRunState.Passed;
    }
}
