using Octokit;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Providers;

/// <summary>
/// Reads GitHub Actions workflow run status via the GitHub REST API.
/// Supports both static token and dynamic token provider (GitHub App auth).
/// </summary>
public class GitHubActionsPipelineProvider : IPipelineProvider
{
    private readonly string? _apiUrl;
    private readonly Func<CancellationToken, Task<string>>? _tokenProvider;
    private readonly IGitHubClient? _client;
    private readonly string _owner;
    private readonly string _repo;
    private readonly TimeSpan _pollInterval;
    private readonly Serilog.ILogger _logger;

    /// <summary>
    /// Creates a provider with a token provider delegate (for GitHub App auth).
    /// </summary>
    public GitHubActionsPipelineProvider(
        string apiUrl,
        Func<CancellationToken, Task<string>> tokenProvider,
        string owner,
        string repo,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _apiUrl = apiUrl;
        _tokenProvider = tokenProvider;
        _owner = owner;
        _repo = repo;
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
    }

    /// <summary>
    /// Creates a provider with a static token.
    /// </summary>
    public GitHubActionsPipelineProvider(
        string apiUrl,
        string token,
        string owner,
        string repo,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(apiUrl);
        ArgumentNullException.ThrowIfNull(token);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _owner = owner;
        _repo = repo;
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
        _client = new GitHubClient(new ProductHeaderValue("KiroWebUI-Pipeline"), new Uri(apiUrl))
        {
            Credentials = new Credentials(token)
        };
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitHubClient.
    /// </summary>
    internal GitHubActionsPipelineProvider(
        IGitHubClient client,
        string owner,
        string repo,
        TimeSpan pollInterval,
        Serilog.ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(repo);

        _client = client;
        _owner = owner;
        _repo = repo;
        _pollInterval = pollInterval;
        _logger = logger ?? Serilog.Log.Logger;
    }

    public async Task<PipelineRunStatus> GetRunStatusAsync(
        string branchName, string? commitSha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        var client = await GetClientAsync(ct);
        var request = new WorkflowRunsRequest { Branch = branchName };
        var runs = await client.Actions.Workflows.Runs.List(_owner, _repo, request);

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
            var runJobs = await client.Actions.Workflows.Jobs.List(_owner, _repo, run.Id);
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

                    // Automatically fetch logs for failed jobs so callers get actionable diagnostics
                    if (status.State == PipelineRunState.Failed)
                    {
                        await EnrichFailedJobsWithLogsAsync(status, linkedCt);
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

    /// <summary>
    /// Fetches full log content from the GitHub Actions API for each failed job
    /// and populates <see cref="PipelineJobResult.LogContent"/>.
    /// Failures are logged and swallowed — missing logs should never block the pipeline.
    /// </summary>
    private async Task EnrichFailedJobsWithLogsAsync(
        PipelineRunStatus status, CancellationToken ct)
    {
        var failedJobs = status.Jobs.Where(j => j.State == PipelineRunState.Failed && j.JobId > 0).ToList();
        if (failedJobs.Count == 0)
            return;

        var client = await GetClientAsync(ct);

        foreach (var job in failedJobs)
        {
            try
            {
                var rawLog = await client.Actions.Workflows.Jobs.GetLogs(_owner, _repo, job.JobId);
                if (!string.IsNullOrEmpty(rawLog))
                {
                    job.LogContent = rawLog;
                    _logger.Debug("Fetched {Length} chars of logs for failed job '{JobName}' (id={JobId})",
                        rawLog.Length, job.Name, job.JobId);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to fetch logs for job '{JobName}' (id={JobId})", job.Name, job.JobId);
            }
        }
    }

    private async Task<IGitHubClient> GetClientAsync(CancellationToken ct)
    {
        if (_tokenProvider is not null)
        {
            var token = await _tokenProvider(ct);
            return new GitHubClient(
                new ProductHeaderValue("KiroWebUI-Pipeline"),
                new Uri(_apiUrl!))
            {
                Credentials = new Credentials(token)
            };
        }

        return _client!;
    }

    internal static PipelineRunState MapJobState(WorkflowJobStatus status, WorkflowJobConclusion? conclusion)
    {
        if (status == WorkflowJobStatus.Queued) return PipelineRunState.Pending;
        if (status == WorkflowJobStatus.InProgress) return PipelineRunState.Running;

        // Completed — check conclusion
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
